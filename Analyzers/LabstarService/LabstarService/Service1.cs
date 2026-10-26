using BactecFXService;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
//using Telegram.Bot;
//using Telegram.Bot.Extensions.Polling;
//using Telegram.Bot.Types;
//using Telegram.Bot.Types.Enums;

namespace LabstarService
{
    public partial class Service1 : ServiceBase
    {
        public Service1()
        {
            InitializeComponent();
            this.ServiceName = "DDriver Labstar";
        }

        #region settings
        public static bool ServiceIsActive;        // флаг для запуска и остановки потока
        public static string AnalyzerResultPath = AppDomain.CurrentDomain.BaseDirectory + "\\AnalyzerResults"; // папка для файлов с результатами
        public static bool FileToErrorPath;        // флаг для перемещения файлов в ошибки или архив
        public static List<Thread> ListOfThreads = new List<Thread>(); // список работающих потоков

        public static string AnalyzerCode = "LABSTAR"; // код прибора 

        public static string user = "PSMExchangeUser"; // логин для базы обмена файлами FNC (FileExchange dbo) и для базы CGM Analytix
        public static string password = "PSM_123456";  // пароль для базы обмена файлами FNC (FileExchange dbo) и для базы CGM Analytix 

        static object ServiceLogLocker = new object();       // локер для логов драйвера
        static object FileResultLogLocker = new object();    //локер для логов функции

        // public static TelegramBotClient botClient = new TelegramBotClient("5713460548:AAHAem3It_bVQQrMcRvX2QNy7n5m_IUqLMY"); // токен бота

        public static YandexMessenger yandexMessengerBot;
        public static CancellationTokenSource cts = new CancellationTokenSource();
        #endregion

        #region функции логов
        static void ServiceLog(string Message)
        {
            lock (ServiceLogLocker)
            {
                try
                {
                    string path = AppDomain.CurrentDomain.BaseDirectory + "\\Logs\\Service";
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }

                    string filename = path + "\\ServiceThread_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";
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
                catch
                {

                }
            }
        }

        // Лог обработки xml файлов и записи результатов в CGM
        static void FileResultLog(string Message)
        {
            try
            {
                lock (FileResultLogLocker)
                {
                    //string path = AppDomain.CurrentDomain.BaseDirectory + "\\Log\\FileResult" + "\\" + DateTime.Now.Year + "\\" + DateTime.Now.Month;
                    string path = AppDomain.CurrentDomain.BaseDirectory + "\\Logs\\FileResult";
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }

                    //string filename = path + $"\\{FileName}" + ".txt";
                    string filename = path + $"\\ResultLog_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";

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

        // проверка старых логов
        public void CheckOldLog()
        {
            while (ServiceIsActive)
            {
                try
                {
                    ServiceLog("Old logs checking");
                    //сегодня минус 28 дней
                    //все что старше удаляем
                    DateTime now = DateTime.Now.AddDays(-28);
                    string LogsDirectory = AppDomain.CurrentDomain.BaseDirectory + "\\Logs\\Service";

                    if (Directory.Exists(LogsDirectory))
                    {
                        string[] files = Directory.GetFiles(LogsDirectory);
                        foreach (string s in files)
                        {
                            DateTime FileDate = System.IO.File.GetCreationTime(s);
                            int comparison = FileDate.CompareTo(now);
                            if (comparison == -1)
                            {
                                System.IO.File.Delete(s);
                            }

                        }
                    }

                }
                catch (Exception LogError)
                {
                    ServiceLog("Ошибка: " + LogError);
                }
                Thread.Sleep(4320000);
            }

        }

        #endregion

        #region вспомогательные функции

        // Интерпретация результата
        static int ResultInterpretation(string result)
        {
            switch (result)
            {
                case "положительный":
                    return 3113;
                case "Положительный":
                    return 3113;
                // если результат "отрицательный"
                // Код МО "Микроорганизмы не обнаружены"
                default:
                    return 89;
            }
        }

        #endregion

        #region Функция проверки работы потоков
        // Поток для проверки потоков чтения порта и обработки файлов с результатами
        public static void CheckThreads()
        {
            while (ServiceIsActive)
            {
                Thread.Sleep(60000);

                List<Thread> ListOfThreadsSearch = new List<Thread>();
                foreach (Thread th in ListOfThreads)
                {
                    ListOfThreadsSearch.Add(th);
                }
                foreach (Thread th in ListOfThreadsSearch)
                {
                    if (!th.IsAlive)
                    {
                        ServiceLog($"The thread {th.Name} is fucking dead");
                        try
                        {
                            if (th.Name == "Result Getter")
                            {
                                ListOfThreads.Remove(th);
                                Thread NewThread = new Thread(ResultGetterFunction);
                                NewThread.Name = th.Name;
                                ListOfThreads.Add(NewThread);
                                NewThread.Start();
                            }
                            if (th.Name == "ResultsProcessing")
                            {
                                ListOfThreads.Remove(th);
                                Thread NewThread = new Thread(ResultsProcessing);
                                NewThread.Name = th.Name;
                                ListOfThreads.Add(NewThread);
                                NewThread.Start();
                            }
                        }
                        catch (Exception e)
                        {
                            ServiceLog($"Can not start thread {th.Name}: {e}");
                        }
                    }
                    else
                    {
                        ServiceLog($"Thread {th.Name} is working");
                    }
                }
                ListOfThreadsSearch.Clear();
            }
        }
        #endregion

        /*

        #region Телеграм-бот для рассылки уведомлений

        // отправка уведомлений пользователям
        public static async Task SendNotification(ITelegramBotClient botClient, string chat, string rid, string pid, string client, string fio)
        {
            //int chat_ = Int32.Parse(chat);
            // id канала
            long chat_ = Int64.Parse(chat);

            //string messageText = "Положительный флакон №: " + rid;
            string messageText = "Положительный флакон №: " + rid + "\n" + "Пациент: " + fio + "\n" + "Id пациента: " + pid + "\n" + "Код отделения: " + client;

            // Echo received message text
            Message sentMessage = await botClient.SendTextMessageAsync(
                                                                        chat_,
                                                                        messageText
                                                                        );
        }

        #endregion
        */

        #region вспомогательные функции SQL

        //апдейтим статус
        public static void SQLUpdateStatus(int IDParam, SqlConnection DBconnection)
        {
            SqlCommand UpdateStatusCommand = new SqlCommand(@"UPDATE FileExchange SET [Status]=1 where id = @id", DBconnection);
            UpdateStatusCommand.Parameters.AddWithValue("@id", IDParam);
            UpdateStatusCommand.ExecuteNonQuery();
            ServiceLog("Статус записи изменен");

        }

        //удаляем из очереди
        public static void SQLDelete(int IDParam, SqlConnection DBconnection)
        {
            SqlCommand DeleteCommand = new SqlCommand(@"Delete FROM FileExchange where id = @id", DBconnection);
            DeleteCommand.Parameters.AddWithValue("@id", IDParam);
            DeleteCommand.ExecuteNonQuery();
            ServiceLog("Запись удалена из таблицы FileExchange");
            ServiceLog("");
        }

        #endregion

        #region запись данных в CGM

        // Регистрация заявки в CGM
        public static void RegistrationInCGM(string RID, SqlConnection CGMConnection)
        {
            //Сначала получаем необходимые данные

            #region данные из таблицы autolid
            // получение данных из таблицы autolid
            string aut_senast = "";
            string aut_stopp; // максимальное значение счетчика
            int aut_aktuallitet = 0;

            SqlCommand GetFromAutolid = new SqlCommand("SELECT a.aut_stopp, a.aut_senast, a.aut_aktualitet FROM autolid a WHERE a.aut_typ = 'SECTION'", CGMConnection);
            SqlDataReader AutolidReader = GetFromAutolid.ExecuteReader();

            if (AutolidReader.HasRows)
            {
                while (AutolidReader.Read())
                {
                    aut_stopp = AutolidReader.GetString(0);
                    int autstopp = Convert.ToInt32(aut_stopp);

                    aut_senast = AutolidReader.GetString(1);
                    aut_aktuallitet = AutolidReader.GetInt32(2);

                    // преобразование счетчика из aut_senast из str в int
                    int aut_senast_counter = Convert.ToInt32(aut_senast);
                    aut_senast_counter++; // инкремент счетчика

                    if (aut_senast_counter <= autstopp)
                    {
                        aut_senast = aut_senast_counter.ToString();
                        if (aut_senast.Length < aut_stopp.Length)
                        {
                            // формируем счетчик определенной длины (6) с учетом нулей 
                            while (aut_senast.Length != aut_stopp.Length)
                            {
                                aut_senast = "0" + aut_senast;
                            }
                        }
                    }

                }

            }
            AutolidReader.Close();

            #endregion

            #region данные из таблицы identitet
            int id_senast = 0;
            int id_aktualitet = 0;

            SqlCommand GetFromIdentitet = new SqlCommand("SELECT i.id_senast, i.id_aktualitet FROM identitet i WHERE i.id_namn = 'prv_id'", CGMConnection);
            SqlDataReader IdentitetReader = GetFromIdentitet.ExecuteReader();

            if (IdentitetReader.HasRows)
            {
                while (IdentitetReader.Read())
                {
                    id_senast = IdentitetReader.GetInt32(0);
                    id_aktualitet = IdentitetReader.GetInt32(1);
                }
            }
            IdentitetReader.Close();

            #endregion

            #region данные из searchview

            // Данные для последующих апдейтов
            int rem_id = 0;
            int pro_id = 0;
            string TestCode = "";
            string adr_kod = "";

            SqlCommand GetFromSearchview = new SqlCommand(
                    "SELECT s.rem_id, s.pro_id, s.ana_analyskod, s.prov_adr_kod_regvid FROM LABETT..searchview s " +
                    "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                    "WHERE s.rem_rid = @rid AND s.bes_ank_dttm IS NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMConnection);

            GetFromSearchview.Parameters.Add(new SqlParameter("@rid", RID));
            SqlDataReader GetFromSearchviewReader = GetFromSearchview.ExecuteReader();

            if (GetFromSearchviewReader.HasRows)
            {
                while (GetFromSearchviewReader.Read())
                {
                    if (!GetFromSearchviewReader.IsDBNull(0))
                    {
                        rem_id = GetFromSearchviewReader.GetInt32(0);
                    }
                    if (!GetFromSearchviewReader.IsDBNull(1))
                    {
                        pro_id = GetFromSearchviewReader.GetInt32(1);
                    }
                    if (!GetFromSearchviewReader.IsDBNull(2))
                    {
                        TestCode = GetFromSearchviewReader.GetString(2);
                    }
                    if (!GetFromSearchviewReader.IsDBNull(3))
                    {
                        adr_kod = GetFromSearchviewReader.GetString(3);
                    }
                }
            }
            GetFromSearchviewReader.Close();
            #endregion

            // В двух блоках ниже - данные для инсерта в provnr, счетчик YYSSSNNNNNN
            #region данные из таблицы provnr

            int prv_id = 0; // счетчик
            string year;    // переменная текущего года, для формирования счетчика prv_prvnr

            SqlCommand GetFromProvnr = new SqlCommand("SELECT MAX(p.prv_id) FROM provnr p", CGMConnection);
            SqlDataReader ProvnrReader = GetFromProvnr.ExecuteReader();

            if (ProvnrReader.HasRows)
            {
                while (ProvnrReader.Read())
                {
                    prv_id = ProvnrReader.GetInt32(0);
                }
            }
            ProvnrReader.Close();

            #endregion

            #region данные из таблицы metod, получение кода секции для теста, формирование счетчика для таблицы provnr

            string sek_kod = "";
            int meg_id = 0;         // id, по которому определяем, какие среды должны быть добавлены 
            string prv_provnr = ""; //счетчик prv_provnr таблицы provnr

            SqlCommand GetFromMetod = new SqlCommand("SELECT m.sek_kod, m.meg_id FROM metod m WHERE m.ana_analyskod = @test_code", CGMConnection);
            GetFromMetod.Parameters.Add(new SqlParameter("@test_code", TestCode));
            SqlDataReader MetodReader = GetFromMetod.ExecuteReader();

            if (MetodReader.HasRows)
            {
                while (MetodReader.Read())
                {
                    sek_kod = MetodReader.GetString(0);
                    meg_id = MetodReader.GetInt32(1);
                }
            }
            MetodReader.Close();
            prv_provnr = DateTime.Now.ToString("yy") + sek_kod + aut_senast;

            #endregion

            #region данные из таблицы ana, saving time
            string ana_spartid = ""; // saving time

            SqlCommand GetFromAna = new SqlCommand("SELECT a.ana_spartid FROM ana a WHERE a.ana_analyskod = @test_code", CGMConnection);
            GetFromAna.Parameters.Add(new SqlParameter("@test_code", TestCode));
            SqlDataReader AnaReader = GetFromAna.ExecuteReader();

            if (AnaReader.HasRows)
            {
                while (AnaReader.Read())
                {
                    ana_spartid = AnaReader.GetString(0);
                }
            }
            AnaReader.Close();
            #endregion

            #region данные из таблицы mediagroup_media, формируем словарь со средами, которые нужно будет добавить в таблицу plate

            //Dictionary<string, int> culture_plate = new Dictionary<string, int>();
            var culture_plate = new Dictionary<string, int>();

            SqlCommand GetFromMediagroup_media = new SqlCommand("SELECT mm.mea_code, mm.mem_sort_order FROM mediagroup_media mm WHERE mm.meg_id = @meg_id", CGMConnection);
            GetFromMediagroup_media.Parameters.Add(new SqlParameter("@meg_id", meg_id));
            SqlDataReader Mediagroup_mediaReader = GetFromMediagroup_media.ExecuteReader();

            string mea_code = "";
            int sort_order = 0;

            if (Mediagroup_mediaReader.HasRows)
            {
                while (Mediagroup_mediaReader.Read())
                {
                    mea_code = Mediagroup_mediaReader.GetString(0);
                    sort_order = Mediagroup_mediaReader.GetInt16(1);
                    culture_plate.Add(mea_code, sort_order);
                }
            }
            Mediagroup_mediaReader.Close();

            #endregion

            #region регистрация заявки в CGM, с учетом полученных значений
            // начало транзакции
            SqlTransaction RequestRegistrationTransaction = CGMConnection.BeginTransaction();

            // обновление таблицы autolid
            SqlCommand UpdateAutolid = CGMConnection.CreateCommand();
            UpdateAutolid.CommandText = "UPDATE dbo.autolid " +
                                            "SET aut_aktualitet = @aut_aktuallitet + 1, aut_chg_time = GETDATE(), aut_chg_user = 'ADMIN', aut_senast = @aut_senast " +
                                            "WHERE aut_typ = 'SECTION' AND aut_aktualitet = @aut_aktuallitet";
            UpdateAutolid.Parameters.Add(new SqlParameter("@aut_aktuallitet", aut_aktuallitet));
            UpdateAutolid.Parameters.Add(new SqlParameter("@aut_senast", aut_senast));
            UpdateAutolid.Transaction = RequestRegistrationTransaction;


            // обновление таблицы identitet
            SqlCommand UpdateIdentitet = CGMConnection.CreateCommand();
            UpdateIdentitet.CommandText = "UPDATE identitet " +
                                            "WITH(UPDLOCK, ROWLOCK) " +
                                            "SET id_senast = @id_senast + 1, id_chg_time = GETDATE(), id_chg_user = 'SCRIPT', id_aktualitet = @id_aktualitet + 1 " +
                                          "WHERE id_namn = 'prv_id' and id_aktualitet = @id_aktualitet";
            UpdateIdentitet.Parameters.Add(new SqlParameter("@id_senast", id_senast));
            UpdateIdentitet.Parameters.Add(new SqlParameter("@id_aktualitet", id_aktualitet));
            UpdateIdentitet.Transaction = RequestRegistrationTransaction;

            // обновление таблицы remiss
            SqlCommand UpdateRemiss = CGMConnection.CreateCommand();
            UpdateRemiss.CommandText = "UPDATE remiss " +
                                            "SET rem_rid = @rid, adr_kod_ankreg = '41', rem_ank_dttm = GETDATE(), " +
                                            "rem_ankstatus = 'Z', adr_kod_ragare = '41', rem_debdat = GETDATE(), " +
                                            "rem_chg_time = GETDATE(), rem_chg_user = 'dbo', rem_aktualitet = '1' " +
                                         "WHERE rem_id = @rem_id and rem_aktualitet = '0'";
            UpdateRemiss.Parameters.Add(new SqlParameter("@rid", RID));
            UpdateRemiss.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            UpdateRemiss.Transaction = RequestRegistrationTransaction;

            // обновление таблицы prov
            SqlCommand UpdateProv = CGMConnection.CreateCommand();
            UpdateProv.CommandText = "UPDATE prov " +
                                        "SET sig_sign_ankomstreg = 'BACTEC', pro_ankomst_dttm = GETDATE(), adr_kod_ankomstlab = '41', " +
                                        "pro_chg_time = GETDATE(), pro_chg_user = 'dbo', pro_aktualitet = '1' " +
                                      "WHERE pro_id = @pro_id and pro_aktualitet = '0'";
            UpdateProv.Parameters.Add(new SqlParameter("@pro_id", pro_id));
            UpdateProv.Transaction = RequestRegistrationTransaction;

            // добавление данных в таблицу provnr
            SqlCommand InsertProvnr = CGMConnection.CreateCommand();
            InsertProvnr.CommandText = "INSERT INTO provnr ( prv_id, prv_provnr, prv_crt_time, prv_crt_user ) " +
                                       "VALUES (@prv_id, @prv_provnr, GETDATE(), 'dbo')";
            InsertProvnr.Parameters.Add(new SqlParameter("@prv_id", prv_id + 1));
            InsertProvnr.Parameters.Add(new SqlParameter("@prv_provnr", prv_provnr));
            InsertProvnr.Transaction = RequestRegistrationTransaction;

            // обновление таблицы bestall
            SqlCommand UpdateBestall = CGMConnection.CreateCommand();
            UpdateBestall.CommandText = "UPDATE bestall " +
                                            "SET sig_sign_anksign = 'BACTEC', bes_ank_dttm = GETDATE(), adr_kod_ankreg = '41', " +
                                            "bes_spartid = DATEADD(day, @saving_time, CAST(GETDATE() AS DATE)), adr_kod_bagare = '41', prv_id = @prv_id, " +
                                            "bes_chg_time = GETDATE(), bes_chg_user = 'dbo', bes_aktualitet = '1' " +
                                        "WHERE pro_id = @pro_id and rem_id = @rem_id and ana_analyskod = @test_code and bes_aktualitet = '0'";
            UpdateBestall.Parameters.Add(new SqlParameter("@saving_time", Int32.Parse(ana_spartid)));
            UpdateBestall.Parameters.Add(new SqlParameter("@prv_id", prv_id));
            UpdateBestall.Parameters.Add(new SqlParameter("@pro_id", pro_id));
            UpdateBestall.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            UpdateBestall.Parameters.Add(new SqlParameter("@test_code", TestCode));
            UpdateBestall.Transaction = RequestRegistrationTransaction;

            //  удаление из таблицы reportreceiver
            SqlCommand DeleteReportreceiver_1 = CGMConnection.CreateCommand();
            DeleteReportreceiver_1.CommandText = "DELETE FROM reportreceiver where rem_id=@rem_id and adr_kod=@client";
            DeleteReportreceiver_1.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            DeleteReportreceiver_1.Parameters.Add(new SqlParameter("@client", adr_kod));
            DeleteReportreceiver_1.Transaction = RequestRegistrationTransaction;

            SqlCommand DeleteReportreceiver_2 = CGMConnection.CreateCommand();
            DeleteReportreceiver_2.CommandText = "DELETE FROM reportreceiver where rem_id=@rem_id and adr_kod='MICROB2'";
            DeleteReportreceiver_2.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            DeleteReportreceiver_2.Transaction = RequestRegistrationTransaction;

            // добавление данных в таблицу reportreceiver
            SqlCommand InsertReportreceiver = CGMConnection.CreateCommand();
            InsertReportreceiver.CommandText = "INSERT INTO reportreceiver ( rem_id, adr_kod, phy_id, repr_type, sig_sign, repr_crt_user ) " +
                                               "VALUES(@rem_id, @client, NULL, 'F', 'BACTEC', 'dbo')";
            InsertReportreceiver.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            InsertReportreceiver.Parameters.Add(new SqlParameter("@client", adr_kod));
            InsertReportreceiver.Transaction = RequestRegistrationTransaction;

            // добавление данных в таблицу plate
            SqlCommand InsertPlate = CGMConnection.CreateCommand();
            InsertPlate.CommandText = "INSERT INTO plate ( " +
                                            "rem_id, pro_id, ana_analyskod, " +
                                            "mea_code, pla_fin_id_count, sig_sign, " +
                                            "pla_crt_user, pla_crt_time, pla_chg_user, " +
                                            "pla_chg_time, pla_chg_version, pla_media_sort_order ) " +
                                         "VALUES (" +
                                            "@rem_id, @pro_id, @testcode, " +
                                            "@mea, '0', 'BACTEC', " +
                                            "'dbo', GETDATE(), 'dbo', " +
                                            "GETDATE(), '0', @sort_order)";
            InsertPlate.Transaction = RequestRegistrationTransaction;

            // выполнение скриптов
            try
            {
                UpdateAutolid.ExecuteNonQuery();
                UpdateIdentitet.ExecuteNonQuery();
                UpdateRemiss.ExecuteNonQuery();
                UpdateProv.ExecuteNonQuery();
                InsertProvnr.ExecuteNonQuery();
                UpdateBestall.ExecuteNonQuery();
                DeleteReportreceiver_1.ExecuteNonQuery();
                DeleteReportreceiver_2.ExecuteNonQuery();
                InsertReportreceiver.ExecuteNonQuery();

                foreach (var plate in culture_plate)
                {
                    string meacode = plate.Key;
                    int sortorder = plate.Value;
                    InsertPlate.Parameters.Clear();
                    InsertPlate.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                    InsertPlate.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                    InsertPlate.Parameters.Add(new SqlParameter("@testcode", TestCode));
                    InsertPlate.Parameters.Add(new SqlParameter("@mea", meacode));
                    InsertPlate.Parameters.Add(new SqlParameter("@sort_order", sortorder));
                    InsertPlate.ExecuteNonQuery();
                }

                // завершение операций после выполнения
                RequestRegistrationTransaction.Commit();

                // запись в лог
                FileResultLog($"Request {RID} is registered in CGM.");
            }
            catch (Exception ex)
            {
                RequestRegistrationTransaction.Rollback();
                FileResultLog($"{ex}");
                FileResultLog($"");
            }
            #endregion
        }

        // Подтверждение заявки в CGM
        public static void ValidationCGM(string rid, int remid, int proid, string testcode, SqlConnection CGMConnection)
        {
            // начало транзакции
            SqlTransaction RequestValidationTransaction = CGMConnection.BeginTransaction();

            // Обновление таблицы bestall
            SqlCommand UpdateBestall = CGMConnection.CreateCommand();

            UpdateBestall.CommandText = "UPDATE bestall " +
                                            "SET met_kod = @testcode, bes_antal = '1', bes_svarstyp = 'R', bes_svarstat = 'G', ste_kod = NULL, " +
                                            "bes_m_dttm = GETDATE(), sig_sign_msign = 'BACTEC', sig_sign_tsign = 'BACTEC', bes_t_dttm = GETDATE(), bes_utskrift = 'S', " +
                                            "adr_alt_flag1 = NULL, adr_alt_flag2 = NULL, adr_alt_flag3 = NULL, bes_avreg = 'X', adr_kod_bagare = '41', " +
                                            "bes_ursprsign = 'ADMIN', bes_chg_time = GETDATE(), bes_chg_user = 'dbo', bes_aktualitet = bes_aktualitet + 1  " +
                                        "WHERE pro_id=@pro_id and rem_id=@rem_id and ana_analyskod=@testcode";

            UpdateBestall.Parameters.Add(new SqlParameter("@testcode", testcode));
            UpdateBestall.Parameters.Add(new SqlParameter("@pro_id", proid));
            UpdateBestall.Parameters.Add(new SqlParameter("@rem_id", remid));
            //UpdateBestall.Parameters.Add(new SqlParameter("@test_code", testcode));

            UpdateBestall.Transaction = RequestValidationTransaction;

            // Обновление таблицы plate
            SqlCommand UpdatePlateTable = CGMConnection.CreateCommand();
            UpdatePlateTable.CommandText = "UPDATE plate " +
                                            "SET sig_sign = 'BACTEC', pla_chg_user = 'dbo', pla_chg_time = GETDATE(), pla_chg_version = pla_chg_version + 1 " +
                                           "where rem_id = @rem_id and pro_id = @pro_id and ana_analyskod = '@testcode'";
            UpdatePlateTable.Parameters.Add(new SqlParameter("@rem_id", remid));
            UpdatePlateTable.Parameters.Add(new SqlParameter("@pro_id", proid));
            UpdatePlateTable.Parameters.Add(new SqlParameter("@testcode", testcode));

            UpdatePlateTable.Transaction = RequestValidationTransaction;

            // Инсерт в таблицу svarrid
            SqlCommand InsertSvarrid = CGMConnection.CreateCommand();
            InsertSvarrid.CommandText = "INSERT INTO svarrid ( rem_id, sri_status, sri_crt_user, sri_chg_user ) VALUES ( @rem_id, 'O', 'dbo', 'dbo' )";
            InsertSvarrid.Parameters.Add(new SqlParameter("@rem_id", remid));

            InsertSvarrid.Transaction = RequestValidationTransaction;

            // выполнение скриптов
            try
            {
                UpdateBestall.ExecuteNonQuery();
                UpdatePlateTable.ExecuteNonQuery();
                InsertSvarrid.ExecuteNonQuery();

                RequestValidationTransaction.Commit();
                FileResultLog("Result was approved by BactecFX analyzer.");
            }
            catch (Exception ex)
            {
                RequestValidationTransaction.Rollback();
                FileResultLog($"{ex}");
                FileResultLog($"Result was not approved.");
            }
        }

        // Предварительный отчет в CGM
        public static void PreliminaryReportCGM(string rid, string pid, int remid, int proid, string testcode, SqlConnection CGMConnection)
        {
            // формируем guid
            Guid guid = Guid.NewGuid();
            //Console.WriteLine(guid);
            // получение данных для последующих инсертов
            int exs_id = 0;
            int bes_id = 0;
            string adr_kod = ""; // код отделения

            // данные из таблицы identitet
            SqlCommand GetFromIdentitet = new SqlCommand("SELECT id_senast FROM identitet WHERE id_namn = 'exs_id'", CGMConnection);
            SqlDataReader IdentitetReader = GetFromIdentitet.ExecuteReader();

            if (IdentitetReader.HasRows)
            {
                while (IdentitetReader.Read())
                {
                    exs_id = IdentitetReader.GetInt32(0);
                    exs_id = exs_id + 1;
                    //Console.WriteLine(exs_id);
                }
            }
            IdentitetReader.Close();
            // данные из serchview
            SqlCommand GetFromSearchV = new SqlCommand("SELECT s.adr_kod_svar1, s.bes_id FROM searchview s " +
                                                       "WHERE s.rem_rid = @rid AND s.rem_id = @rem_id AND pro_id = @pro_id AND s.ana_analyskod = @testcode", CGMConnection);
            GetFromSearchV.Parameters.Add(new SqlParameter("@rid", rid));
            GetFromSearchV.Parameters.Add(new SqlParameter("@rem_id", remid));
            GetFromSearchV.Parameters.Add(new SqlParameter("@pro_id", proid));
            GetFromSearchV.Parameters.Add(new SqlParameter("@testcode", testcode));
            SqlDataReader SearchviewReader = GetFromSearchV.ExecuteReader();

            if (SearchviewReader.HasRows)
            {
                while (SearchviewReader.Read())
                {
                    adr_kod = SearchviewReader.GetString(0);
                    bes_id = SearchviewReader.GetInt32(1);
                    //Console.WriteLine(adr_kod);
                    //Console.WriteLine(bes_id);
                }
            }
            SearchviewReader.Close();

            // начало транзакции
            SqlTransaction PreliminaryReportTransaction = CGMConnection.BeginTransaction();

            // Обновление bestall
            SqlCommand UpdateBestall = CGMConnection.CreateCommand();
            UpdateBestall.CommandText = "UPDATE bestall " +
                                        "SET adr_kod_bagare = '41', bes_chg_time = GETDATE(), bes_chg_user = 'ADMIN', bes_aktualitet = bes_aktualitet + 1, bes_send_prel_reply = '2', " +
                                        "bes_send_prel_reply_auto = 'O', sig_sign_sent_prel_reply = 'BACTEC', bes_ordered_prel_reply_dttm = GETDATE() " +
                                        "where pro_id = @pro_id and rem_id = @rem_id and ana_analyskod = @testcode";

            UpdateBestall.Parameters.Add(new SqlParameter("@pro_id", proid));
            UpdateBestall.Parameters.Add(new SqlParameter("@rem_id", remid));
            UpdateBestall.Parameters.Add(new SqlParameter("@testcode", testcode));
            UpdateBestall.Transaction = PreliminaryReportTransaction;

            // Обновление identitet
            SqlCommand UpdateIdentitet = CGMConnection.CreateCommand();
            UpdateIdentitet.CommandText = "UPDATE identitet " +
                                          "SET id_chg_user = 'SCRIPT', id_chg_time = GETDATE(), id_senast = id_senast + 1, id_aktualitet = id_aktualitet + 1 " +
                                          "WHERE(id_senast + 1 <= id_tom or id_tom IS null) AND id_namn = 'exs_id'";
            UpdateIdentitet.Transaction = PreliminaryReportTransaction;

            // INSERT svar
            SqlCommand InsertSvar = CGMConnection.CreateCommand();
            InsertSvar.CommandText = "INSERT INTO dbo.svar (" +
                                        "adr_kod, exs_id, pop_pid, rem_id, rem_rid, sva_aktualitet, sva_andr_tid, sva_chg_time, sva_chg_user, " +
                                        "sva_crt_time, sva_crt_user, sva_flag, sva_flag_changed, sva_flagnr, sva_id, sva_prel_reply_guid, sva_svarstyp, sva_use_redirection ) " +
                                     "VALUES(" +
                                        "@adr_kod, @exs_id, @pid, @rem_id, @rid, 0, GETDATE(), GETDATE(), 'dbo', " +
                                        "GETDATE(), 'dbo', 'X X X  X', NULL, NULL, @exs_id, @guid, 'P', NULL)";
            InsertSvar.Parameters.Add(new SqlParameter("@adr_kod", adr_kod));
            InsertSvar.Parameters.Add(new SqlParameter("@exs_id", exs_id));
            InsertSvar.Parameters.Add(new SqlParameter("@pid", pid));
            InsertSvar.Parameters.Add(new SqlParameter("@rem_id", remid));
            InsertSvar.Parameters.Add(new SqlParameter("@rid", rid));
            InsertSvar.Parameters.Add(new SqlParameter("@guid", guid));
            InsertSvar.Transaction = PreliminaryReportTransaction;

            // INSERT extrasvar
            SqlCommand InsertExtrasvar = CGMConnection.CreateCommand();
            InsertExtrasvar.CommandText = "INSERT INTO dbo.extrasvar ( " +
                                            "adr_kod, adr_kod_exs, apro_id, arem_id, exs_crt_time, exs_crt_user, exs_id, exs_provdat_from, " +
                                            "exs_provdat_tom, exs_separate_printer, exs_svarsatt, inr_id, phy_id, pop_pid, pro_id, rem_id ) " +
                                          "VALUES(@adr_kod, '41', NULL, NULL, GETDATE(), 'ADMIN', @exs_id, NULL, NULL, 0, 'D', NULL, NULL, @pid, NULL, @rem_id)";
            InsertExtrasvar.Parameters.Add(new SqlParameter("@adr_kod", adr_kod));
            InsertExtrasvar.Parameters.Add(new SqlParameter("@exs_id", exs_id));
            InsertExtrasvar.Parameters.Add(new SqlParameter("@pid", pid));
            InsertExtrasvar.Parameters.Add(new SqlParameter("@rem_id", remid));
            InsertExtrasvar.Transaction = PreliminaryReportTransaction;

            // INSERT extraana
            SqlCommand InsertExtraana = CGMConnection.CreateCommand();
            InsertExtraana.CommandText = "INSERT INTO dbo.extraana ( ana_analyskod, apro_id, arem_id, exa_crt_time, exa_crt_user, exs_id, pro_id, rem_id ) " +
                                         "VALUES ( @testcode, NULL, NULL, GETDATE(), 'ADMIN', @exs_id, @pro_id, @rem_id )";
            InsertExtraana.Parameters.Add(new SqlParameter("@testcode", testcode));
            InsertExtraana.Parameters.Add(new SqlParameter("@exs_id", exs_id));
            InsertExtraana.Parameters.Add(new SqlParameter("@pro_id", proid));
            InsertExtraana.Parameters.Add(new SqlParameter("@rem_id", remid));
            InsertExtraana.Transaction = PreliminaryReportTransaction;

            // INSERT testorderforpreliminaryreply
            SqlCommand InsertTestOrderForPreliminaryReply = CGMConnection.CreateCommand();
            InsertTestOrderForPreliminaryReply.CommandText = "INSERT INTO dbo.testorderforpreliminaryreply ( bes_id, prel_reply_guid, topr_crt_time, topr_crt_user ) " +
                                                             "VALUES ( @bes_id, @guid, GETDATE(), 'ADMIN' )";
            InsertTestOrderForPreliminaryReply.Parameters.Add(new SqlParameter("@bes_id", bes_id));
            InsertTestOrderForPreliminaryReply.Parameters.Add(new SqlParameter("@guid", guid));
            InsertTestOrderForPreliminaryReply.Transaction = PreliminaryReportTransaction;

            // выполнение скриптов
            try
            {
                UpdateBestall.ExecuteNonQuery();
                UpdateIdentitet.ExecuteNonQuery();
                InsertSvar.ExecuteNonQuery();
                InsertExtrasvar.ExecuteNonQuery();
                InsertExtraana.ExecuteNonQuery();
                InsertTestOrderForPreliminaryReply.ExecuteNonQuery();

                PreliminaryReportTransaction.Commit();
                FileResultLog("Preliminary report was sent to Request source.");
            }
            catch (Exception ex)
            {
                PreliminaryReportTransaction.Rollback();
                FileResultLog($"{ex}");
                FileResultLog($"Preliminary report was NOT sent to Request source.");
            }
        }

        // Запись результатов в CGM
        public static void InsertResultToCGM(string InsertRid, string InsertResult)
        {
            int ResultForInsert = ResultInterpretation(InsertResult); // интерпретация результата
            //bool RIDExist = false;
            bool IsCultureTest = false; // флаг посевного теста
            FileToErrorPath = false;    // флаг 

            try
            {
                //string CGMConnectionString = @"Data Source=CGM-DATA02; Initial Catalog=LABETT; Integrated Security=True; User Id = PSMExchangeUser; Password = PSM_123456";

                //string CGMConnectionString = @"Data Source=CGM-DATA02; Initial Catalog=LABETT; Integrated Security=True;";
                //string CGMConnectionString = @"Data Source = CGM-DATA01\CGMSQL; Initial Catalog = LABETT;";

                string CGMConnectionString = ConfigurationManager.ConnectionStrings["CGMConnection"].ConnectionString;
                CGMConnectionString = String.Concat(CGMConnectionString, $"User Id = {user}; Password = {password}");

                using (SqlConnection CGMconnection = new SqlConnection(CGMConnectionString))
                {
                    CGMconnection.Open();

                    #region Проверяем RID

                    int TestCount = 0; // переменная для подсчета кол-ва зарегистрированных микробиологических тестов

                    // Проверяем RID. Микробиологический тест должен быть в заявке, быть зарегистрирован и не подтвержден.
                    SqlCommand RIDExistCommand = new SqlCommand(
                                "SELECT s.rem_rid, s.ana_analyskod, s.bes_ank_dttm, s.bes_reg_dttm, s.bes_t_dttm, a.dis_kod " +
                                "FROM LABETT..searchview s " +
                                "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod" +
                                $" WHERE s.rem_rid = '{InsertRid}' AND s.bes_ank_dttm IS NOT NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);

                    SqlDataReader Reader = RIDExistCommand.ExecuteReader();

                    // Если такой (такие) тест(ы) есть, то продолжаем работу
                    if (Reader.HasRows)
                    {
                        IsCultureTest = true;
                        FileResultLog($"Request {InsertRid} with culture test is registered, test is not validated.");
                        Reader.Close();
                    }
                    else
                    {
                        Reader.Close();
                        //Запись в лог , что такая заявка не зарегана в CGM
                        FileResultLog($"Request {InsertRid} with culture test is NOT registered in CGM (or test was validated)");

                        int CulTestcount = 0;   // переменная для подсчета кол-ва незарегистрированных микробиологических тестов в заявке
                        bool IsСulTest = false; // флаг для определения, есть ли посевные тесты в заявке

                        // Проверяем, есть ли в принципе микробиологические тесты в заявке (и в принципе заявка)
                        // Если они есть и не зарегистрированы, то нужно посчитать кол-во тестов, если тест один - зарегистрировать его.
                        SqlCommand CultureTestInRequestCommand = new SqlCommand(
                            "SELECT s.rem_rid, s.ana_analyskod, s.bes_ank_dttm, s.bes_reg_dttm, s.bes_t_dttm, a.dis_kod " +
                            "FROM LABETT..searchview s INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                            "WHERE s.rem_rid = @rid AND s.bes_reg_dttm IS NOT NULL AND s.bes_ank_dttm IS NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);

                        CultureTestInRequestCommand.Parameters.Add(new SqlParameter("@rid", InsertRid));
                        SqlDataReader CultureTestInRequestReader = CultureTestInRequestCommand.ExecuteReader();

                        if (CultureTestInRequestReader.HasRows)
                        {
                            IsСulTest = true; //В заявке есть микробиологические тесты
                        }
                        else
                        {
                            FileResultLog($"Request {InsertRid} does not contain culture tests (or tests were validated)");
                            // убираем, так как много заявок в результтирующем файле
                            //FileToErrorPath = true; //флаг указывает на то, что файл будет перемещен в папку с ошибками
                        }
                        CultureTestInRequestReader.Close();
                        //FileToErrorPath = true; //флаг указывает на то, что файл будет перемещен в папку с ошибками

                        // Если в заявке есть микробилоогические тесты, нужно посчитать кол-во, регистрируется только один тест
                        if (IsСulTest)
                        {
                            SqlCommand CulTestCountCommand = new SqlCommand(
                                "SELECT COUNT(*) FROM LABETT..searchview s " +
                                "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                                "WHERE s.rem_rid = @rid AND s.bes_ank_dttm IS NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);
                            CulTestCountCommand.Parameters.Add(new SqlParameter("@rid", InsertRid));
                            SqlDataReader CulTestCountReader = CulTestCountCommand.ExecuteReader();

                            if (CulTestCountReader.HasRows)
                            {
                                while (CulTestCountReader.Read())
                                {
                                    CulTestcount = CulTestCountReader.GetInt32(0);
                                }
                            }
                            CulTestCountReader.Close();

                            // Если тест один
                            if (CulTestcount == 1)
                            {
                                FileResultLog($"Request {InsertRid} exists and need to be registered.");
                                // функция регистрации заявки
                                RegistrationInCGM(InsertRid, CGMconnection);
                                // флаг того, что в заявке есть зарегистрированный микробиологический тест, чтобы продолжить выполнение после регистрации
                                IsCultureTest = true;
                            }
                            else
                            {
                                FileResultLog($"Request {InsertRid} contains more than one culture test. Unable to register.");
                            }
                        }
                    }
                    //Reader.Close();

                    // Если зарегистрирован микробиологический тест
                    if (IsCultureTest)
                    {
                        // Проверяем, один ли посевный тест зарегистрирован в заявке
                        SqlCommand CultureTestCount = new SqlCommand(
                            "SELECT COUNT(*) FROM LABETT..searchview s " +
                            "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                            $"WHERE s.rem_rid = '{InsertRid}' AND s.bes_ank_dttm IS NOT NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);

                        Reader = CultureTestCount.ExecuteReader();

                        // int TestCount = 0;
                        if (Reader.HasRows)
                        {
                            while (Reader.Read())
                            {
                                TestCount = Reader.GetInt32(0);
                            }
                            FileResultLog($"{TestCount} culture test in the request.");
                        }
                        Reader.Close();
                    }
                    #endregion

                    // Если зарегистрирован микробиологический тест и кол-во тестов в заявке = 1
                    if (IsCultureTest && TestCount == 1)
                    {
                        #region pro_id, rem_id, тест, lid
                        // находим pro_id, rem_id, тест, lid
                        int rem_id = 0;
                        int pro_id = 0;
                        string TestCode = "";
                        string Lid = "";
                        string pid = "";

                        SqlCommand GetData = new SqlCommand(
                            "SELECT s.rem_id, s.pro_id, s.ana_analyskod, s.pro_provid, s.pop_pid FROM LABETT..searchview s " +
                            "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                            $"WHERE s.rem_rid = '{InsertRid}' AND s.bes_ank_dttm IS NOT NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);

                        SqlDataReader DataReader = GetData.ExecuteReader();

                        if (DataReader.HasRows)
                        {
                            while (DataReader.Read())
                            {
                                rem_id = DataReader.GetInt32(0);
                                pro_id = DataReader.GetInt32(1);
                                TestCode = DataReader.GetString(2);
                                Lid = DataReader.GetString(3);
                                pid = DataReader.GetString(4);
                            }
                        }
                        DataReader.Close();
                        #endregion

                        #region Проверка среды

                        // проверяем, есть ли среда Bactec, если нет - добавляем
                        bool IsMediaExist = false; // флаг наличия среды 

                        SqlCommand GetMedia = new SqlCommand($"SELECT * FROM LABETT..plate p WHERE p.rem_id = {rem_id} AND p.mea_code = 'BT_BLOOD'", CGMconnection);
                        SqlDataReader MediaReader = GetMedia.ExecuteReader();

                        if (MediaReader.HasRows)
                        {
                            IsMediaExist = true;
                            //Console.WriteLine("Среда Бактек есть в заявке");
                            //FileResultLog($"Среда BACTEC есть в заявке");
                            MediaReader.Close();
                        }
                        else
                        {
                            MediaReader.Close();
                            SqlTransaction InsertMediaBactec = CGMconnection.BeginTransaction();
                            SqlCommand InsertBactec = CGMconnection.CreateCommand();

                            InsertBactec.Transaction = InsertMediaBactec;

                            InsertBactec.CommandText = $"INSERT INTO LABETT..plate VALUES ({rem_id}, {pro_id}, '{TestCode}', 'BT_BLOOD', 'SCRIPT', 'dbo', GETDATE(), 0, 'dbo', GETDATE(), 1, 2)";
                            InsertBactec.ExecuteNonQuery();

                            InsertMediaBactec.Commit();
                            IsMediaExist = true;

                            //Console.WriteLine("Среда Бактек добавлена в заявку");
                            FileResultLog($"BACTEC media is inserted.");
                        }
                        #endregion

                        if (IsMediaExist)
                        {
                            #region Кол-во микроорганизмов в среде BACTEC

                            int finCount = 0;

                            SqlCommand GetMOCount = new SqlCommand($"SELECT COUNT(*) FROM LABETT..finding f WHERE f.rem_id = {rem_id} AND f.mea_code = 'BT_BLOOD'", CGMconnection);
                            SqlDataReader MOCountReader = GetMOCount.ExecuteReader();

                            if (MOCountReader.HasRows)
                            {
                                while (MOCountReader.Read())
                                {
                                    finCount = MOCountReader.GetInt32(0);
                                    //Console.WriteLine($"{finCount} микроорганизма в среде"); ;
                                }
                            }
                            MOCountReader.Close();

                            #endregion

                            #region Проверяем есть ли такие микроорганизмы в среде
                            SqlCommand CheckMO = new SqlCommand(
                                        "SELECT fin_id from  finding where rem_id=@rem_id " +
                                        "and pro_id=@pro_id AND ana_analyskod = @test_code and mea_code='BT_BLOOD' and fyt_id=@fyt_id ", CGMconnection);
                            CheckMO.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                            CheckMO.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                            CheckMO.Parameters.Add(new SqlParameter("@test_code", TestCode));
                            CheckMO.Parameters.Add(new SqlParameter("@fyt_id", ResultForInsert));
                            SqlDataReader CheckMOReader = CheckMO.ExecuteReader();

                            bool Is_There_MO = CheckMOReader.HasRows;
                            CheckMOReader.Close();
                            #endregion

                            // Если микроорганизма нет в среде, то Insert
                            if (!Is_There_MO)
                            {
                                FileResultLog("Getting data from tables prov, plate, findings");

                                #region Получение данных из таблицы prov

                                int pro_fin_count = 0;
                                int pro_aktualitet = 0;

                                SqlCommand GetProvData = new SqlCommand("SELECT pro_fin_count, pro_aktualitet FROM prov WHERE pro_id = @pro_id ", CGMconnection);
                                GetProvData.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                SqlDataReader ProvReader = GetProvData.ExecuteReader();
                                if (ProvReader.HasRows)
                                {
                                    while (ProvReader.Read())
                                    {
                                        if (!ProvReader.IsDBNull(0))
                                        {
                                            //pro_fin_count = ProvReader.GetInt32(0);
                                            pro_fin_count = ProvReader.GetInt16(0);
                                        }
                                        if (!ProvReader.IsDBNull(1))
                                        {
                                            pro_aktualitet = ProvReader.GetInt16(1);
                                        }
                                    }
                                }
                                ProvReader.Close();

                                #endregion

                                #region Получение данных из таблицы plate
                                int fin_id_count = 0;
                                int chg_version = 0;
                                SqlCommand GetPlateData = new SqlCommand("SELECT pla_fin_id_count, pla_chg_version FROM LABETT..plate p WHERE p.rem_id = @rem_id" +
                                    " and pro_id = @pro_id AND ana_analyskod = @test_code AND p.mea_code = 'BT_BLOOD'", CGMconnection);
                                GetPlateData.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                GetPlateData.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                GetPlateData.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                SqlDataReader PlateReader = GetPlateData.ExecuteReader();
                                if (PlateReader.HasRows)
                                {
                                    while (PlateReader.Read())
                                    {
                                        if (!PlateReader.IsDBNull(0))
                                        {
                                            fin_id_count = PlateReader.GetInt16(0);
                                        }
                                        if (!PlateReader.IsDBNull(1))
                                        {
                                            chg_version = PlateReader.GetInt16(1);
                                        }
                                    }
                                }
                                PlateReader.Close();

                                // инкремент счетчиков
                                fin_id_count++;
                                chg_version++;

                                // определяем значение в столбце fin_number, сумма значений pla_fin_id_count
                                int fin_number = 0;
                                SqlCommand fin_numberCount = new SqlCommand(
                                    "select SUM(pla_fin_id_count) from plate " +
                                    "where rem_id=@rem_id and pro_id=@pro_id and ana_analyskod = @test_code " +
                                    "GROUP BY rem_id ", CGMconnection);
                                fin_numberCount.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                fin_numberCount.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                fin_numberCount.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                SqlDataReader fin_numberCountReader = fin_numberCount.ExecuteReader();
                                if (fin_numberCountReader.HasRows)
                                {
                                    while (fin_numberCountReader.Read())
                                    {
                                        if (!fin_numberCountReader.IsDBNull(0))
                                        {
                                            fin_number = fin_numberCountReader.GetInt32(0);
                                        }
                                    }
                                }
                                fin_numberCountReader.Close();
                                fin_number++;
                                #endregion

                                #region Получение данных из таблицы finding
                                // Если уже какие-то микроорганизмы есть в среде, то получаем данные счетчиков из таблицы
                                // Если МО нет, то просто увеличиваем счетчик, для последующего insert
                                int fin_sort_order = 0;

                                SqlCommand GetFindingData = new SqlCommand("SELECT MAX(fin_sort_order) from finding " +
                                            "WHERE rem_id=@rem_id and pro_id=@pro_id ", CGMconnection);
                                GetFindingData.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                GetFindingData.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                SqlDataReader FindingReader = GetFindingData.ExecuteReader();
                                if (FindingReader.HasRows)
                                {
                                    while (FindingReader.Read())
                                    {
                                        if (!FindingReader.IsDBNull(0))
                                        {
                                            fin_sort_order = FindingReader.GetInt16(0);
                                        }
                                    }
                                }
                                FindingReader.Close();
                                fin_sort_order++;

                                #endregion

                                FileResultLog("Data from tables are received");

                                // Запись данных в БД CGM

                                FileResultLog("Inserting results...");

                                SqlTransaction InsertMOTransaction = CGMconnection.BeginTransaction();

                                #region Update таблицы prov
                                SqlCommand UpdateProv = CGMconnection.CreateCommand();
                                UpdateProv.CommandText = "UPDATE prov SET pro_fin_count = @pro_fin_count, pro_chg_time = GETDATE(), " +
                                                         "pro_aktualitet = @pro_aktualitet " +
                                                         "where pro_id = @pro_id and pro_aktualitet = @old_pro_aktualitet";
                                UpdateProv.Parameters.Add(new SqlParameter("@pro_fin_count", pro_fin_count + 1));
                                UpdateProv.Parameters.Add(new SqlParameter("@pro_aktualitet", pro_aktualitet + 1));
                                UpdateProv.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                UpdateProv.Parameters.Add(new SqlParameter("@old_pro_aktualitet", pro_aktualitet));
                                UpdateProv.Transaction = InsertMOTransaction;
                                #endregion

                                #region Update таблицы plate
                                // сначала апдейт основных данных в среде Bactec
                                SqlCommand UpdatePlate = CGMconnection.CreateCommand();
                                UpdatePlate.CommandText = "Update plate SET pla_fin_id_count = @fin_id_count, sig_sign = 'SCRIPT', " +
                                    "pla_chg_user = 'dbo', pla_chg_time = GETDATE(), pla_chg_version = @chg_version, pla_media_sort_order = '2' " +
                                    "where rem_id=@rem_id and pro_id = @pro_id and ana_analyskod=@test_code and mea_code='BT_BLOOD' ";
                                UpdatePlate.Parameters.Add(new SqlParameter("@fin_id_count", fin_id_count));
                                UpdatePlate.Parameters.Add(new SqlParameter("@chg_version", chg_version));
                                UpdatePlate.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                UpdatePlate.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                UpdatePlate.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                UpdatePlate.Transaction = InsertMOTransaction;

                                // апдейт chg_version для других сред
                                SqlCommand UpdatePlate_others = CGMconnection.CreateCommand();
                                UpdatePlate_others.CommandText = "UPDATE plate SET sig_sign = 'SCRIPT', pla_chg_user = 'dbo', pla_chg_time = GETDATE(), pla_chg_version = pla_chg_version+1 " +
                                    "where rem_id=@rem_id and pro_id= @pro_id and ana_analyskod=@test_code and mea_code <>'BT_BLOOD'";
                                UpdatePlate_others.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                UpdatePlate_others.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                UpdatePlate_others.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                UpdatePlate_others.Transaction = InsertMOTransaction;

                                #endregion

                                #region Insert МО в таблицу finding

                                SqlCommand InsertMO = CGMconnection.CreateCommand();
                                InsertMO.CommandText = "INSERT INTO finding ( " +
                                    "rem_id, pro_id, ana_analyskod, mea_code, fin_id, " +
                                    "fyt_id, fin_sort_order, amo_id, fin_fin_comment_int, fin_fin_comment_ext, " +
                                    "fin_res_comment, fin_origin, fin_reply, fin_include_amount_on_reports, sig_sign, " +
                                    "fin_analys_dttm, fin_number, fin_crt_user, fin_crt_time, fin_chg_user, " +
                                    "fin_chg_time, fin_chg_version, culture_performing_laboratory ) " +
                                    "VALUES ( " +
                                    "@rem_id, @pro_id, @test_code, 'BT_BLOOD', @fin_id_count, " +
                                    "@fyt_id, @fin_sort_order, NULL, NULL, NULL, " +
                                    "NULL, 'BACTEC', 'X', '1', 'BACTEC', " +
                                    "GETDATE(), @fin_number, 'dbo', GETDATE(), 'dbo', " +
                                    "GETDATE(), '0', '41' )";
                                InsertMO.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                InsertMO.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                InsertMO.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                InsertMO.Parameters.Add(new SqlParameter("@fin_id_count", fin_id_count));
                                InsertMO.Parameters.Add(new SqlParameter("@fyt_id", ResultForInsert));
                                InsertMO.Parameters.Add(new SqlParameter("@fin_sort_order", fin_sort_order));
                                InsertMO.Parameters.Add(new SqlParameter("@fin_number", fin_number));
                                InsertMO.Transaction = InsertMOTransaction;

                                #endregion

                                try
                                {
                                    UpdateProv.ExecuteNonQuery();
                                    UpdatePlate.ExecuteNonQuery();
                                    UpdatePlate_others.ExecuteNonQuery();
                                    InsertMO.ExecuteNonQuery();
                                    InsertMOTransaction.Commit();

                                    // запись в лог
                                    FileResultLog($"Result {InsertResult} is inserted.");
                                    //FileResultLog($"");
                                }
                                catch (Exception ex)
                                {
                                    InsertMOTransaction.Rollback();
                                    FileResultLog($"{ex}");
                                    FileResultLog($"Result {InsertResult} is NOT inserted!");
                                    //FileResultLog($"");
                                }

                                #region Отправка сообщения в бот
                                if (InsertResult == "положительный" || InsertResult == "Положительный")
                                {
                                    #region Получем из базы данные по заявке для оповещения

                                    string Request = "";
                                    string FIO = "";
                                    string ClientCode = "";
                                    string PatientId = "";

                                    SqlCommand GetNotificationData = new SqlCommand("SELECT r.rem_rid, r.pop_pid, r.adr_kod_svar1, p.pop_enamn + ' ' + p.pop_fnamn AS FIO " +
                                                                                        "FROM LABETT..remiss r INNER JOIN labett..pop p ON r.pop_pid = p.pop_pid " +
                                                                                        "WHERE r.rem_rid = @rid", CGMconnection);
                                    GetNotificationData.Parameters.Add(new SqlParameter("@rid", InsertRid));
                                    SqlDataReader GetNotificationDataReader = GetNotificationData.ExecuteReader();

                                    if (GetNotificationDataReader.HasRows)
                                    {
                                        while (GetNotificationDataReader.Read())
                                        {
                                            Request = GetNotificationDataReader.GetString(0);
                                            PatientId = GetNotificationDataReader.GetString(1);
                                            ClientCode = GetNotificationDataReader.GetString(2);
                                            FIO = GetNotificationDataReader.GetString(3);
                                        }
                                    }
                                    GetNotificationDataReader.Close();

                                    #endregion

                                    // отправка оповещения, если результат Positive

                                    /*
                                    var appSettings = ConfigurationManager.AppSettings;

                                    foreach (var key in appSettings.AllKeys)
                                    {
                                        SendNotification(botClient, appSettings[key], Request, PatientId, ClientCode, FIO);
                                    }
                                    */

                                    // Отправка сообщения в Яндекс мессенджер
                                    yandexMessengerBot.SendMessageAsync(Request, PatientId, ClientCode, FIO);

                                    FileResultLog($"Notification was sent to BactecFX_bot.");
                                    //FileResultLog($"");
                                }

                                #endregion

                            }
                            // Если микроорганизм уже есть, то ничего не записываем
                            else
                            {
                                FileResultLog("Result is already exists.");
                                //FileResultLog($"");
                            }

                            // Если результат NEGATIVE - валидируем результат, если POSITIVE - предварительный отчет
                            // Если POSITIVE и такого микроорганизма еще нет в среде, то необходимо отправить предварительный отчет
                            if ((InsertResult == "отрицательный")||(InsertResult == "Отрицательный"))
                            {
                                ValidationCGM(InsertRid, rem_id, pro_id, TestCode, CGMconnection);
                            }
                            else if ((InsertResult == "положительный" && !Is_There_MO)||(InsertResult == "Положительный" && !Is_There_MO))
                            {
                                PreliminaryReportCGM(InsertRid, pid, rem_id, pro_id, TestCode, CGMconnection);
                            }
                            FileResultLog($"");
                        }
                    }
                    else
                    {
                        // невозможно записать данные в CGM
                        FileResultLog($"Impossible to insert data to CGM");
                        FileResultLog($"");
                        // убираем, так как много результатов в одном файле
                        //FileToErrorPath = true; //флаг указывает на то, что файл будет перемещен в папку с ошибками
                    }

                    CGMconnection.Close();
                }
            }
            catch (Exception Error)
            {
                FileResultLog($"{Error}");
            }
        }

        #endregion

        #region функция, которая сохраняет в папку AnalyzerResult файлы с результатами из базы FNC
        public static void ResultGetterFunction()
        {
            while (ServiceIsActive)
            {
                try
                {
                    //ServiceLog("Ожидание файлов с результатами от Labstar");

                    // если нет папки для результатов, создаем её
                    if (!Directory.Exists(AnalyzerResultPath))
                    {
                        Directory.CreateDirectory(AnalyzerResultPath);
                    }

                    string ExchangeDBConnectionString = ConfigurationManager.ConnectionStrings["ExchangeDBConnection"].ConnectionString;
                    ExchangeDBConnectionString = String.Concat(ExchangeDBConnectionString, $"User Id = {user}; Password = {password}");

                    try
                    {
                        using (SqlConnection FNCconnection = new SqlConnection(ExchangeDBConnectionString))
                        {
                            FNCconnection.Open();

                            SqlCommand GetResultFiles = new SqlCommand($"SELECT TOP 1 fe.id, fe.FileName, [File] FROM FileExchange fe " +
                                           $"WHERE fe.ServiceID like '{AnalyzerCode}' and fe.Status = 0 ORDER BY ChangeDate", FNCconnection);
                            SqlDataReader Reader = GetResultFiles.ExecuteReader();

                            int id = 0;
                            string FileName = "";
                            byte[] resData = { };

                            if (Reader.HasRows)
                            {
                                while (Reader.Read())
                                {
                                    if (!Reader.IsDBNull(0)) { id = Reader.GetInt32(0); }
                                    if (!Reader.IsDBNull(1)) { FileName = Reader.GetString(1); };
                                    if (!Reader.IsDBNull(2)) { resData = (byte[])Reader.GetValue(2); }

                                    string DirFileName = $"{AnalyzerResultPath}\\" + FileName;
                                    using (FileStream fs = new FileStream(DirFileName, FileMode.OpenOrCreate))
                                    {
                                        fs.Write(resData, 0, resData.Length);
                                    }

                                    ServiceLog($"Файл с результатами: {FileName}");
                                    FileResultLog($"");
                                }

                                Reader.Close();
                                SQLUpdateStatus(id, FNCconnection);
                                SQLDelete(id, FNCconnection);
                            }

                            FNCconnection.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceLog(ex.ToString());
                    }
                }
                catch (Exception ex)
                {
                    //ServiceLog(ex.Message);
                    ServiceLog(ex.ToString());
                }

                Thread.Sleep(30000);
            }
        }

        #endregion

        #region обработчик xml
        public static void XMLHandler(string filepath)
        {
            FileToErrorPath = false;

            try
            {
                // объект для входящего файла xml
                XmlDocument xDoc = new XmlDocument();
                // загружаем в объект текущий файл xml
                xDoc.Load(filepath);
                // получим корневой элемент
                XmlElement xRoot = xDoc.DocumentElement;

                if (xRoot != null)
                {
                    // обход всех узлов в корневом элементе
                    foreach (XmlElement xnode in xRoot)
                    {
                        //Console.WriteLine(xnode.Name);
                        //FileResultLog(xnode.Name);
                    }

                    // выбор в документе всех узлов с именем ROWDATA и элементами ROW
                    XmlNodeList rowdata = xRoot.SelectNodes("//ROWDATA//ROW");
                    if (rowdata != null)
                    {
                        foreach (XmlNode row in rowdata)
                        {
                            // выбор узла, содержащего RID
                            //string RID = row.SelectSingleNode("@FIELD_1")?.Value;
                            string RID = row.SelectSingleNode("@ID")?.Value;
                            // проверка шк на корректность, должен состоять из цифр
                            bool isNumber = long.TryParse(RID, out long numericValue);
                            // выбор узла, содержащего результат
                            string Result = row.SelectSingleNode("@FIELD_8")?.Value;

                            // если шк состоит из цифр и есть какой-то результат 
                            if (isNumber && Result != "Нет_результата")
                            {
                                // Если RID > 10 знаков, значит это LID (или просто кривой номер??) и обрезаем до 10 знаков
                                if (RID.Length > 10)
                                {
                                    RID = RID.Substring(0, 10);
                                }
                                // элемент в котором обнаружен результат
                                FileResultLog(row.OuterXml);
                                //FileResultLog($"RID: {row.SelectSingleNode("@FIELD_1")?.Value}");
                                FileResultLog($"RID: {row.SelectSingleNode("@ID")?.Value}");
                                FileResultLog($"Result: {row.SelectSingleNode("@FIELD_8")?.Value}");

                                // Запись результатов в CGM
                                InsertResultToCGM(RID, Result);

                                #region Отправка сообщения в бот

                                #endregion

                            }
                        }
                    }
                    else
                    {
                        // нет узлов с именем ROWDATA и элементами ROW, файл нужно поместить в папку error
                        FileResultLog($"Failed to get //ROWDATA//ROW elements");
                        FileToErrorPath = true;
                    }
                }
                else
                {
                    // не получили корневой элемент, файл нужно поместить в папку error
                    FileResultLog($"Failed to get the root element.");
                    FileToErrorPath = true;
                }
            }
            catch (Exception ex)
            {
                FileResultLog("File processing error." + "\n" + ex.ToString());
                FileToErrorPath = true;
            }
        }
        #endregion

        #region обработка файлов с результатами ResultsProcessing
        public static void ResultsProcessing()
        {
            while (ServiceIsActive)
            {
                try
                {
                    if (!Directory.Exists(AnalyzerResultPath))
                    {
                        Directory.CreateDirectory(AnalyzerResultPath);
                    }

                    string ArchivePath = AnalyzerResultPath + @"\Archive";
                    string ErrorPath = AnalyzerResultPath + @"\Error";

                    if (!Directory.Exists(ArchivePath))
                    {
                        Directory.CreateDirectory(ArchivePath);
                    }

                    if (!Directory.Exists(ErrorPath))
                    {
                        Directory.CreateDirectory(ErrorPath);
                    }

                    string[] Files = Directory.GetFiles(AnalyzerResultPath, "*.xml");

                    foreach (string file in Files)

                    {
                        // FileToErrorPath = false;

                        Console.WriteLine(file);
                        FileResultLog(file);

                        string FileName = file.Substring(AnalyzerResultPath.Length + 1);
                        //Console.WriteLine($"File name {FileName}");
                        //FileResultLog($"File: {FileName}");

                        // поиск данных в каждом файле и далее запись результатов в CGM (передаем путь к файлу)
                        XMLHandler(file);
                        //XMLHandler(@"C:\Users\anton.petuhov\source\repos\Labstar_Console\Labstar_Console\bin\Debug\Results\TestLabstar.xml");

                        // Перемещение файлов в архив или ошибки
                        if (!FileToErrorPath)
                        {
                            if (System.IO.File.Exists(ArchivePath + @"\" + FileName))
                            {
                                System.IO.File.Delete(ArchivePath + @"\" + FileName);
                            }
                            System.IO.File.Move(file, ArchivePath + @"\" + FileName);
                            //Console.WriteLine("File has been moved to Archive folder.");
                            FileResultLog("File has been moved to Archive folder.");
                        }
                        else
                        {
                            if (System.IO.File.Exists(ErrorPath + @"\" + FileName))
                            {
                                System.IO.File.Delete(ErrorPath + @"\" + FileName);
                            }
                            System.IO.File.Move(file, ErrorPath + @"\" + FileName);
                            //Console.WriteLine("File has been moved to Error folder.");
                            FileResultLog("File has been moved to Error folder.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileResultLog(ex.ToString());
                }

                Thread.Sleep(1000);
            }
        }

        #endregion


        protected override void OnStart(string[] args)
        {
            ServiceIsActive = true;
            ServiceLog("Labstar service starts working.");

            /*
            // запуск телеграм-бота
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServiceLog("Telegram Bot started " + botClient.GetMeAsync().Result.FirstName);
            */

            // настраиваем бота яндекс мессенджера
            yandexMessengerBot = new YandexMessenger();
            ServiceLog("Яндекс бот запущен.");

            // Поток, который следит за другими потоками
            Thread ManagerThread = new Thread(CheckThreads);
            ManagerThread.Name = "Thread Manager";
            ManagerThread.Start();

            // ResultsProcessing();
            // ResultGetterFunction();
            // CheckOldLog();

            // Поток удаления старых логов
            Thread OldLogDeleteThread = new Thread(new ThreadStart(CheckOldLog));
            OldLogDeleteThread.Name = "Old logs checking";
            ListOfThreads.Add(OldLogDeleteThread);
            OldLogDeleteThread.Start();

            //Поток, который сохраняет файлы в папку из базы
            Thread ResultGetterThread = new Thread(new ThreadStart(ResultGetterFunction));
            ResultGetterThread.Name = "Result Getter";
            ListOfThreads.Add(ResultGetterThread);
            ResultGetterThread.Start();

            // Поток обработки xml результатов
            Thread ResultProcessingThread = new Thread(ResultsProcessing);
            ResultProcessingThread.Name = "ResultsProcessing";
            ListOfThreads.Add(ResultProcessingThread);
            ResultProcessingThread.Start();

            ServiceLog("Service is working");
        }

        protected override void OnStop()
        {
            ServiceLog("Service is stopped");
            ServiceIsActive = false;
        }
    }
}
