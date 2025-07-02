using QRCoder;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading; // ★ DispatcherTimerのために追加
using WebSocketSharp.Server;

namespace TatehamaRadioPcApp
{
    public partial class MainWindow : Window
    {
        private const string DiscordClientId = "1384881561094324264";
        private const string BackendApiUrl = "https://train-radio.tatehama.jp/api/pc-auth";
        private const string DiscordAuthUrl = "https://discord.com/oauth2/authorize?client_id=1384881561094324264&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A8000%2Fcallback%2F&scope=identify+guilds+guilds.members.read";
        private const string RedirectUri = "http://localhost:8000/callback/";
        
        private static readonly HttpClient httpClient = new HttpClient();

        // ★★★ サーバーとタイマーの変数を追加 ★★★
        private WebSocketServer? wssv;
        private DispatcherTimer? _qrCodeRefreshTimer;
        private int _countdown;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            QrCodePanel.Visibility = Visibility.Collapsed;
            AuthInProgressPanel.Visibility = Visibility.Visible;

            try
            {
                string authCode = await AuthenticateWithDiscordAsync();
                string jwtToken = await GetJwtTokenFromBackendAsync(authCode);
                var userInfo = DecodeJwt(jwtToken);
                
                await Dispatcher.InvokeAsync(() => {
                    UpdateUiForSuccess(userInfo);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => {
                    UpdateUiForError(ex.Message);
                });
            }
        }

        // ★★★ PC版のみで利用するボタンの処理 ★★★
        private void PcOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            // タイマーとサーバーを停止
            _qrCodeRefreshTimer?.Stop();
            wssv?.Stop();
            
            // TODO: ここでPC版のメイン無線画面に遷移する
            MessageBox.Show("PC版単体モードは現在開発中です。");
        }


        // ★★★ QRコードとサーバーを更新するサイクルを開始するメソッド ★★★
        private void StartQrCodeRefreshCycle()
        {
            // 既存のタイマーがあれば停止
            _qrCodeRefreshTimer?.Stop();
            
            // 接続情報を生成し、QRコードを更新
            UpdateConnectionAndQrCode();

            _countdown = 30; // カウントダウンをリセット
            QrCountdownText.Text = $"（{_countdown}秒後に更新されます）";

            // 1秒ごとにカウントダウンするタイマーを開始
            _qrCodeRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _qrCodeRefreshTimer.Tick += (sender, e) => {
                _countdown--;
                if (_countdown > 0)
                {
                    QrCountdownText.Text = $"（{_countdown}秒後に更新されます）";
                }
                else
                {
                    // カウントダウンが0になったら、サイクルを再開
                    StartQrCodeRefreshCycle();
                }
            };
            _qrCodeRefreshTimer.Start();
        }

        // ★★★ 接続情報とQRコードを更新する処理を分離 ★★★
        private void UpdateConnectionAndQrCode()
        {
            // 既存のサーバーがあれば停止
            wssv?.Stop();

            string ipAddress = GetLocalIpAddress();
            int port = 8080; // 実際には空いているポートを動的に探すのが望ましい
            StartWebSocketServer(ipAddress, port);
            string connectionInfo = $"ws://{ipAddress}:{port}";

            var qrCodeImage = GenerateQrCode(connectionInfo);
            QrCodeImage.Source = qrCodeImage;
        }

        // (他のメソッドは変更ありません... AuthenticateWithDiscordAsync, GetJwtTokenFromBackendAsync, etc.)

        private async Task<string> AuthenticateWithDiscordAsync()
        {
            var httpListener = new HttpListener();
            httpListener.Prefixes.Add(RedirectUri);
            httpListener.Start();

            Process.Start(new ProcessStartInfo(DiscordAuthUrl) { UseShellExecute = true });
            
            var context = await httpListener.GetContextAsync();
            var response = context.Response;

            string? code = context.Request.QueryString.Get("code");
            string? error = context.Request.QueryString.Get("error");

            string responseMessage;
            if (!string.IsNullOrEmpty(error))
            {
                responseMessage = $"認証に失敗しました: {error}。このウィンドウを閉じてください。";
            }
            else if (string.IsNullOrEmpty(code))
            {
                responseMessage = "認証コードを取得できませんでした。このウィンドウを閉じて、もう一度お試しください。";
            }
            else
            {
                responseMessage = "認証が完了しました。アプリに戻ってください。";
            }

            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes($"<html><head><meta charset='utf-8'></head><body>{responseMessage}</body></html>");
            response.ContentLength64 = buffer.Length;
            var output = response.OutputStream;
            await output.WriteAsync(buffer, 0, buffer.Length);
            output.Close();
            httpListener.Stop();

            if (!string.IsNullOrEmpty(error)) throw new Exception($"Discordからエラーが返されました: {error}");
            if (string.IsNullOrEmpty(code)) throw new Exception("Discord認証コードの取得に失敗しました。");
            
            return code;
        }

        private async Task<string> GetJwtTokenFromBackendAsync(string code)
        {
            var requestData = new { code };
            var content = new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, BackendApiUrl)
            {
                Content = content
            };

            var response = await httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var message = "不明なエラー";
                try
                {
                    var errorJson = JsonDocument.Parse(errorContent);
                    message = errorJson.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() : "JSONエラーメッセージなし";
                }
                catch 
                {
                    message = $"サーバーから予期せぬ応答がありました (Status: {response.StatusCode}): {errorContent}";
                }
                throw new Exception($"バックエンドサーバーからの応答エラー: {message}");
            }

            var successContent = await response.Content.ReadAsStringAsync();
            var successJson = JsonDocument.Parse(successContent);
            return successJson.RootElement.GetProperty("token").GetString() ?? throw new Exception("JWTトークンが見つかりません。");
        }

        private UserInfo DecodeJwt(string token)
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new Exception("無効なJWTトークンです。");

            var payload = parts[1];
            
            payload = payload.Replace('-', '+').Replace('_', '/');
            
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var jsonBytes = Convert.FromBase64String(payload);
            var json = Encoding.UTF8.GetString(jsonBytes);
            
            var payloadData = JsonDocument.Parse(json).RootElement;
            
            string? nickname = payloadData.TryGetProperty("guildNickname", out var nick) ? nick.GetString() : null;
            string username = payloadData.GetProperty("username").GetString()!;
            string userId = payloadData.GetProperty("userId").GetString()!;
            string? avatarHash = payloadData.TryGetProperty("avatar", out var av) ? av.GetString() : null;

            return new UserInfo
            {
                DisplayName = nickname ?? username,
                AvatarUrl = avatarHash != null ? $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.png" : null
            };
        }

        private string GetLocalIpAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            throw new Exception("ローカルIPアドレスが見つかりません。");
        }

        private void StartWebSocketServer(string ipAddress, int port)
        {
            wssv = new WebSocketServer($"ws://{ipAddress}:{port}");
            wssv.AddWebSocketService<RadioConnectionBehavior>("/Radio");
            wssv.Start();
        }

        private BitmapImage GenerateQrCode(string text)
        {
            var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new QRCode(qrCodeData);
            Bitmap qrCodeImage = qrCode.GetGraphic(20);

            using (var memory = new MemoryStream())
            {
                qrCodeImage.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
                memory.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                return bitmapImage;
            }
        }

        // ★★★ UI更新の引数からQRコードを削除 ★★★
        private void UpdateUiForSuccess(UserInfo userInfo)
        {
            AuthInProgressPanel.Visibility = Visibility.Collapsed;
            QrCodePanel.Visibility = Visibility.Visible;
            
            WelcomeText.Text = $"ようこそ {userInfo.DisplayName} さん";
            if(userInfo.AvatarUrl != null)
            {
                AvatarImage.ImageSource = new BitmapImage(new Uri(userInfo.AvatarUrl));
            }
            
            // ★★★ QRコード更新サイクルを開始 ★★★
            StartQrCodeRefreshCycle();
        }

        private void UpdateUiForError(string message)
        {
            AuthInProgressPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = message;
        }
    }

    public class UserInfo
    {
        public string DisplayName { get; set; } = "";
        public string? AvatarUrl { get; set; }
    }

    public class RadioConnectionBehavior : WebSocketBehavior
    {
        // WebSocketの通信処理（省略）
    }
}
