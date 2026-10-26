using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using MimeKit;
using MailKit.Net.Smtp;
using System.IO;
using System.Text.Json;

namespace BactecFXService
{
    // Настройки отчёта
    public class MailSettings
    {
        public string[] emailsTo { get; set; }
    }

    public class MailSender
    {
        #region Настройки
        private readonly string configFilePath;
        private readonly MailSettings defaultSettings;

        public const string send_mail_name = "noreply";
        public const string send_mail = "noreply@fccho-moscow.ru";
        public const string send_mail_pswd = "Fsfds#13";

        private string send_mail_subj;
        private string send_mail_text;
        string[] emailsTo;
        #endregion

        public MailSender(string[] emails)
        {
            this.configFilePath = configFilePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var settings = GetSettings("Email");

            emailsTo = settings.emailsTo;

            //emailsTo = emails;

            // Значения по умолчанию
            defaultSettings = new MailSettings
            {
                emailsTo = new[] { "anton.petuhov@dgoi.ru" }
            };

        }

        public async Task SendEmailAsync(string rid, string pid, string clientcode, string fio)
        {
            send_mail_subj = "BacTec: Положительный флакон";
            send_mail_text = "Анализатор: BacTec" + "\n" + "Положительный флакон №: " + rid + "\n" + "Пациент: " + fio + "\n" + "Id пациента: " + pid + "\n" + "Код отделения: " + clientcode;

            foreach (string mailTo in emailsTo)
            {
                try
                {
                    var emailMessage = new MimeMessage();
                    emailMessage.From.Add(new MailboxAddress(send_mail_name, send_mail));
                    emailMessage.To.Add(new MailboxAddress("", mailTo));
                    emailMessage.Subject = send_mail_subj;
                    var builder = new BodyBuilder();
                    builder.TextBody = send_mail_text;
                    //builder.Attachments.Add(file);
                    emailMessage.Body = builder.ToMessageBody();

                    using (MailKit.Net.Smtp.SmtpClient client = new MailKit.Net.Smtp.SmtpClient())
                    {
                        client.Connect("mail.dgoi.ru", 25, false);
                        client.Authenticate(send_mail, send_mail_pswd);
                        client.Send(emailMessage);
                        client.Disconnect(true);
                    }
                }
                catch (Exception ex)
                {

                }
            }
        }

        // Получение настроек для конкретного отчёта по ключу
        public MailSettings GetSettings(string reportKey)
        {
            if (!File.Exists(configFilePath))
            {
                return defaultSettings; // настройки по умолчанию
            }

            try
            {
                string json = File.ReadAllText(configFilePath);
                // Десериализуем в словарь
                var allSettings = JsonSerializer.Deserialize<Dictionary<string, MailSettings>>(json);

                if (allSettings == null || !allSettings.TryGetValue(reportKey, out var settings))
                {
                    return defaultSettings; // настройки по умолчанию
                }
                // Если список получателей для reportKey пустой в json
                if (settings.emailsTo == null || settings.emailsTo.Length == 0)
                {
                    return defaultSettings; // настройки по умолчанию
                }

                return settings;
            }
            catch (Exception ex)
            {
                // Ошибка при чтении файла конфигурации. Используются настройки по умолчанию.
                return defaultSettings; // настройки по умолчанию
            }
        }
    }
}
