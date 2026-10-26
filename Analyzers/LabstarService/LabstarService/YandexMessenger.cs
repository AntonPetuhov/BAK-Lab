using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;


namespace BactecFXService
{
    public class YandexMessengerSettings
    {
        public string chatId { get; set; }
        public string token { get; set; }
    }

    public class YandexMessenger
    {
        private readonly string settingsFilePath;
        private readonly YandexMessengerSettings settings;

        static object locker = new object();

        public YandexMessenger()
        {
            settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            settings = GetSettings("YandexMessenger");
        }

        public YandexMessengerSettings GetSettings(string sectionName)
        {
            try
            {
                string json = File.ReadAllText(settingsFilePath);
                // Десериализуем в словарь
                var allSettings = JsonSerializer.Deserialize<Dictionary<string, YandexMessengerSettings>>(json);
                allSettings.TryGetValue(sectionName, out var settings);

                return settings;
            }
            catch
            {
                return null;
            }
        }

        public async Task SendMessageAsync(string rid, string pid, string clientcode, string fio)
        {
            string chatId = settings.chatId;
            string token = settings.token;

            string baseUrl = "https://botapi.messenger.yandex.net/bot/v1/";
            string requestUrl = $"{baseUrl}messages/sendText/"; // для отправки сообщений

            string messageText = "Анализатор: Юнона" +"\n" +  "Положительный флакон №: " + rid + "\n" + "Пациент: " + fio + "\n" + "Id пациента: " + pid + "\n" + "Код отделения: " + clientcode;

            // botLog($"TEXT {messageText}");

            var body = new
            {
                chat_id = chatId,
                text = messageText
            };

            string json = JsonSerializer.Serialize(body);

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("OAuth", token);

                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await client.PostAsync(requestUrl, content))
                {
                    string responseText = await response.Content.ReadAsStringAsync();

                    //Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
                    //Console.WriteLine(responseText);

                    response.EnsureSuccessStatusCode();
                }
            }  
        }


        // Лог записи результатов в CGM
        static void botLog(string Message)
        {
            try
            {
                lock (locker)
                {
                    //string path = AppDomain.CurrentDomain.BaseDirectory + "\\Log\\FileResult" + "\\" + DateTime.Now.Year + "\\" + DateTime.Now.Month;
                    string path = AppDomain.CurrentDomain.BaseDirectory + "\\Log\\Bot";
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }

                    //string filename = path + $"\\{FileName}" + ".txt";
                    string filename = path + $"\\BotLog_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";

                    if (!System.IO.File.Exists(filename))
                    {
                        using (StreamWriter sw = System.IO.File.CreateText(filename))
                        {
                            sw.WriteLine(DateTime.Now + ": " + Message);
                        }
                    }
                    else
                    {
                        using (StreamWriter sw = System.IO.File.AppendText(filename))
                        {
                            sw.WriteLine(DateTime.Now + ": " + Message);
                        }
                    }
                }
            }
            catch
            {

            }
        }


    }
}
