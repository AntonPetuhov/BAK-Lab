# Internal Integration: построчный разбор и прохождение XML/PDF

Разбор текущей упрощенной версии на 12 октября 2026 года. Здесь объяснены все строки восьми исходных .cs-файлов рабочего приложения, appsettings.json, .csproj и профиля публикации. Служебный .sln, сгенерированные bin/obj и построчное устройство тестовых классов в этот разбор не входят: они не исполняют производственную обработку пары. Пустые строки и скобки также отмечены. Код приложения и исходные примеры при подготовке разбора не изменены.

## 1. Сначала важный факт о вашем примере

В файле `007_2f18ef0c-2ded-4428-8b77-18c3ddcbc303.xml` есть:

```xml
<!DOCTYPE SafirMessage SYSTEM "10repw.dtd">
```

Текущая программа запрещает DTD через `DtdProcessing.Prohibit`. Поэтому именно исходный документ вызывает `XmlException` при `XDocument.Load`, до чтения RequisitionID и проверки AddressCode. Пара остается во входе, задание блокируется. PDF не публикуется. Ранее описанный результат «пример пройдет без изменений, потому что AddressCode=39» без оговорки о DTD был неточен. Приведенное ниже успешное прохождение относится только к специально обозначенной учебной копии БЕЗ строки DOCTYPE.

Это не предложение автоматически разрешить DTD. Изменение такого правила требует отдельного решения. Для просмотра технической структуры использован отдельный reader с Ignore и отключенным XmlResolver: DTD не загружался. Рабочий XmlTransformer не изменялся.

Технические поля примера:

| Поле | Значение |
| --- | --- |
| Requisition/@RequisitionID | 1000097520 |
| Requisition/ReqUnit/@AddressCode | 39 |
| Корневой элемент | SafirMessage |
| Количество заявок | 1 |
| Количество Sample | 3 |
| Количество Analysis | 6 |

| Sample/@LaboratoryID | Analysis/@TestMethodCode |
| --- | --- |
| 1000097520IF | IF_HBSAGT |
| 1000097520IF | IF_HCV |
| 1000097520IF | IF_HIV_(2) |
| 1000097520IF | IF_SYFILIS |
| 1000097520IG | IF_HIV_(2) |
| 1000097520IH | IF_HIV_(2) |

PDF для программы является непрозрачным набором байтов. Она не читает страницы, текст и результаты анализов. Персональные поля XML в этот документ не включены.

## 2. Карта вызовов

```text
Program (точка входа)
  Host.CreateApplicationBuilder
  new FileLogger → Info → Write → FileStream.Append
  ProcessingOptions.Validate → Resolve / Within / пробный файл
  регистрация options, logger, Worker
  Host.StartAsync
    BackgroundService.StartAsync
      Worker.ExecuteAsync
        new PairEngine → открыть .processor.lock
        цикл:
          PairEngine.Scan
            StateStore.Load
            один снимок входной папки
            Ready(XML) → при готовности StateStore.Save(FirstReady)
            для каждого задания Process
              CheckFilesReady → Ready(XML), Ready(PDF) или проверка таймаута
              PrepareFiles
                OpenSource → прочитать XML
                XmlTransformer.Transform
                  XmlReader → XDocument.Load
                  Requisition → ReqUnit → Reply/Sample/Analysis
                  без замен: исходный byte[]
                  с заменами: DetectEncoding → XmlWriter
                временные XML/PDF → SHA256 → FileIdentity → StateStore.Save
              PublishFiles
                PublishOne(PDF) → сохранить намерение → File.Move
                PublishOne(XML) → сохранить намерение → File.Move
                StateStore.Save(Published)
              Verify обоих выходов
              LogResults → FileLogger.Info → Write
              DeleteSources → DeleteVerified(XML/PDF)
              удалить staging и JSON состояния
          Task.Delay(ScanIntervalSeconds)
  Host.WaitForShutdownAsync
    Ctrl+C / SCM / фатальная ошибка → отмена Worker → Dispose
```

Стрелки обозначают вызовы, а не отдельные процессы. Внутри Scan все выполняется последовательно. Пока XML/PDF не готовы, Process возвращает управление и цикл рассматривает следующую пару. Чтение/копирование большого готового файла занимает время и задерживает следующие пары. `async` у Worker нужен для паузы и остановки, а не для параллельного преобразования XML.

## 3. Фактическое прохождение предоставленной пары

Проверено вызовами настоящего PairEngine на временных копиях. Время Scan задавалось программно, поэтому не требовалось реально ждать пять минут. Логгер использовал реальные часы; его timestamps отличаются от моделируемой шкалы Scan. Программа как Windows Service через SCM для этого не устанавливалась.

Предпосылки: оба файла уже скопированы полностью, Output/State пусты, один экземпляр, настройки 10/3/300 секунд, файлы не меняются. Для простоты время первого обхода принято за t=0.

| Обход | Что делает код | Состояние |
| --- | --- | --- |
| t=0 | Scan находит XML; Ready впервые запоминает размер и LastWriteTime | XML еще не готов, JSON нет |
| t=10 | Ready подтверждает XML; сохраняется FirstReady=t10. CheckFilesReady замечает PDF и впервые наблюдает его стабильность | PdfSeen=true; оба исходника на месте; вывода нет |
| t=20 | Готовность обоих подтверждена; PrepareFiles читает XML; Transform вызывает XDocument.Load | DOCTYPE запрещен → XmlException |
| После исключения | Scan увеличивает Failures, сохраняет Blocked и пишет ошибку | Failures=1, Blocked=true, Transaction=null |
| Следующие обходы/перезапуск | Проверка Blocked пропускает задание | Автоматического повторного разбора нет |

Почему не t=3: у службы нет таймера на каждый файл. Минимум стабильности равен 3 секундам, но проверить его служба сможет при следующем обходе. Почему еще один обход для PDF: Ready(PDF) впервые вызывается только после признания XML готовым. Это особенности текущей реализации, а не утверждение, что любая пара всегда занимает ровно 20 секунд.

Существенная строка реального журнала (timestamp опущен):

```text
ERROR | file=007_2f18ef0c-2ded-4428-8b77-18c3ddcbc303.xml | requisitionID=не определён | Окончательная ошибка: XmlException; попытка=1; исходники не удаляются
```

Номер 1000097520 есть внутри файла, но приложение еще не успело его извлечь: он записывается в PairState только после успешного Transform. Даже «Начало обработки» расположено ниже этого вызова и для ошибки DTD не появляется. Строка «Без изменений» также отсутствует: отсутствие успешной обработки не равно успешной обработке без замен.

## 4. Учебное успешное прохождение: копия без DOCTYPE

В отдельной временной копии удалена только строка DOCTYPE; AddressCode оставлен 39. Приложенный исходник не менялся. Учебная копия уже отличается от оригинала отсутствием DOCTYPE, поэтому «байт в байт» далее означает сравнение результата именно с этой учебной копией.

Первые два обхода такие же. На третьем:

1. `PrepareFiles` открывает оба исходника для чтения с запретом параллельной записи.
2. `Transform` успешно загружает SafirMessage, находит единственный Requisition и номер 1000097520.
3. Находит единственный непосредственный ReqUnit с AddressCode=39.
4. Для каждого из шести Analysis проверка `AddressCode != "32"` истинна. `continue` пропускает изменение. До выбора suffix по LaboratoryID дело не доходит.
5. Счетчик `count=0`. Возвращается исходный массив `source`, XmlWriter не создается. `Results` содержит пустой массив Changes и Reason=AddressCode=39.
6. `PrepareFiles` пишет временный `Output/.staging/<GUID>/xml`, копирует PDF в соседний `pdf`, считает хеши и сохраняет идентификаторы NTFS.
7. `StateStore.Save` фиксирует подготовку. `Transaction` теперь содержит GUID, `SourceXmlHash == XmlHash`, `Replacements=0`.
8. `PublishOne` для PDF сохраняет PdfIntent=true и переименовывает временный PDF в итоговое исходное имя.
9. Аналогично сохраняется XmlIntent=true и публикуется XML. Оба результата уже есть, сохраняется Published=true.
10. `Verify` перепроверяет оба итоговых файла; `LogResults` записывает отсутствие изменений и успешную публикацию пары.
11. `DeleteVerified` проверяет исходные хеши и удаляет временные входные копии, сначала XML, затем PDF.
12. Удаляются staging-каталог и JSON. Движок остается работающим до следующего обхода.

Проверка фактического результата: XML совпал байт в байт с учебным входом; PDF совпал байт в байт с предоставленным PDF. Технические строки журнала:

```text
INFO | file=007_2f18ef0c-2ded-4428-8b77-18c3ddcbc303.xml | requisitionID=1000097520 | Начало обработки
INFO | file=007_2f18ef0c-2ded-4428-8b77-18c3ddcbc303.xml | requisitionID=1000097520 | Без изменений: AddressCode=39
INFO | file=007_2f18ef0c-2ded-4428-8b77-18c3ddcbc303.xml | requisitionID=1000097520 | Пара XML/PDF опубликована
INFO | file=007_2f18ef0c-2ded-4428-8b77-18c3ddcbc303.xml | requisitionID=1000097520 | Обработка завершена; исходники удалены
```

## 5. Учебный вариант: PDF так и не пришел

Для успешного XML-only сценария также использована копия БЕЗ DOCTYPE. На t=10 сохраняется FirstReady. До t=310 прошло меньше 300 секунд от этого момента. На t=309 выходного XML еще нет; на t=310 условие таймаута истинно и XML публикуется отдельно. В проверенном прогоне его байты сохранились.

`pdfStream=null`, `PdfHash=null`, копирования/публикации/удаления PDF нет. Запись журнала: `requisitionID=1000097520 | XML опубликован без PDF: срок ожидания истек`. Если бы использовали исходный XML с DOCTYPE, после таймаута он все равно был бы отклонен парсером. Таймаут не отменяет валидацию XML.

Если PDF был замечен, PdfSeen остается true: вместо проверки таймаута выбирается Ready(PDF), даже если файл заблокирован или временно исчез. Поздний PDF после уже завершенной XML-only обработки остается для ручного разбора.

## 6. Учебный вариант: отделение 32

Дополнительно только в памяти учебной копии без DOCTYPE заменен AddressCode именно у ReqUnit с 39 на 32. Вызван настоящий Transform; получено две замены:

| LaboratoryID | Было | Стало | Количество |
| --- | --- | --- | --- |
| 1000097520IG | IF_HIV_(2) | IF_HIV_(2)_2 | 1 |
| 1000097520IH | IF_HIV_(2) | IF_HIV_(2)_1 | 1 |

У 1000097520IF даже тест IF_HIV_(2) не меняется: этого LaboratoryID нет в switch. Остальные три кода у IF также не меняются. Порядок Sample/Analysis не используется. `count=2` приводит к DetectEncoding и XmlWriter, поэтому лексическое оформление XML может измениться. PDF при этом остается обычной байтовой копией.

Нюанс текущего объекта RequisitionResult: поле Reason при AddressCode=32 заполняется текстом «подходящих тестов нет» даже при реальных заменах. Это не выводится в лог, потому что LogResults использует Reason только когда Changes.Length==0. При чтении результата в отладчике сначала смотрите Changes и Replacements.

## 7. Что происходит при остановке и ошибках

| Место прерывания | Что сохранено | Что будет при следующем запуске |
| --- | --- | --- |
| Ожидание PDF | FirstReady, PdfSeen | Новый период проверки стабильности; старый дедлайн остается |
| После Prepared | Временные результаты, хеши, GUID | Подготовка не повторяется; продолжается публикация |
| После PDF, до XML | PDF на выходе и PdfIntent | Проверка PDF по хешу/NTFS, затем публикация XML |
| После обоих файлов | Published и результаты | Проверка выходов, лог, очистка |
| После удаления XML | Опубликованные файлы, JSON, возможно входной PDF | Отсутствующий исходный XML допустим; завершается удаление PDF |
| XML с DTD/поврежденный XML | Blocked=true | Нужен разбор причины оператором; перезапуск не снимает блокировку |
| Отказ текстового журнала | Зависит от уже завершенного этапа | FileLogException поднимается к Worker; после восстановления доступа состояние позволяет продолжить |

Результаты двух File.Move не являются одной атомарной операцией. Поэтому сначала PDF, затем XML. SHA-256 отвечает на вопрос «совпадают ли байты», FileIdentity — «тот ли это файловый объект, который мы подготовили». JSON состояния нельзя заменять текстовым логом: лог не содержит всех контрольных данных и может повторять строки после сбоя.

## 8. Как читать синтаксис

| Запись | Смысл в этом проекте |
| --- | --- |
| public / private / internal | Доступ извне / только внутри класса / внутри сборки |
| sealed | От класса нельзя наследоваться |
| static | Не нужен экземпляр класса |
| readonly | Поле нельзя переназначить после конструктора; содержимое объекта может быть изменяемым |
| var | Тип выводит компилятор; это не динамический тип |
| string? | Допускается отсутствие ссылки null |
| value! | Подавление предупреждения nullable; не проверка и не исправление null в runtime |
| a ?? b | Взять b только если a равен null |
| x?.Method() | Вызвать только если x не null |
| condition ? a : b | Выбрать одну из двух ветвей |
| => | Короткое тело метода или лямбда-функция |
| new() / [] | Создание объекта с известным типом / пустая коллекция |
| using Namespace | Импорт имен, не управление файлом |
| using var stream | Автоматический Dispose при выходе из текущей области |
| foreach / continue / return | Перебор / следующая итерация / выход из текущего метода |
| throw / catch / when | Выброс исключения / перехват / фильтр перехвата |
| await Task.Delay | Неблокирующая пауза, которая поддерживает отмену |
| GroupBy / Select / ToArray | Сгруппировать / преобразовать элементы / материализовать массив |
| $"...{value}..." | Подстановка значения в строку |

Номера далее соответствуют файлам на момент разбора. В каждой таблице приведена оригинальная строка и объяснение. Комментарии и пустые строки не запускают никаких действий. Проверка строк генератором не оставляет неописанных исполняемых конструкций.

## 9. Построчный разбор

### 9.1. Program.cs — Запуск и завершение

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/Program.cs>). Строк: 37. SHA-256: `82a63b200f41adb5d8bf473d63d83ace04727a975414cabd89552604665b2dc7`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:1>) | `using InternalIntegration;` | Подключает имена собственных классов приложения. Это импорт имен, не вызов функции. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:2>) | `using Microsoft.Extensions.Configuration;` | Подключает API чтения и привязки конфигурации. Это импорт имен, не вызов функции. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:3>) | `using Microsoft.Extensions.DependencyInjection;` | Подключает регистрацию зависимостей AddSingleton/AddHostedService. Это импорт имен, не вызов функции. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:4>) | `using Microsoft.Extensions.Hosting;` | Подключает Host и BackgroundService. Это импорт имен, не вызов функции. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:5>) | `using Microsoft.Extensions.Logging;` | Нужен здесь для ClearProviders, чтобы отключить стандартный вывод Host; прикладной ILogger не создается. Это импорт имен, не вызов функции. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:6>) | — | Пустая строка для читаемости; не выполняется. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:7>) | `FileLogger? logger = null;` | Переменная для единственного файлового логгера. Знак ? допускает null: до успешного создания логгера ссылки еще нет. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:8>) | `try` | Начинает защищенный блок: подходящие исключения из него обрабатываются в последующих catch. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:9>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:10>) | `// Конфигурация читается рядом с exe, независимо от рабочей папки службы Windows.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:11>) | `var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings` | Создает стандартный .NET Host builder: конфигурацию, контейнер зависимостей и управление временем жизни. Здесь Worker еще не создан и не запущен. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:12>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:13>) | `Args = args, ContentRootPath = AppContext.BaseDirectory` | Передает аргументы запуска и назначает каталог exe корнем конфигурации. Благодаря этому SCM не заставляет искать appsettings.json в System32. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:14>) | `});` | Завершает инициализатор аргумента и вызов метода. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:15>) | `logger = new FileLogger(builder.Configuration["FileLogging:DirectoryPath"] ?? "Logs");` | Читает путь FileLogging:DirectoryPath; если ключ отсутствует, выбирает Logs. Конструктор FileLogger создает папку и сразу пишет «Журнал открыт». |
| [16](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:16>) | `var options = builder.Configuration.GetSection("FileProcessing").Get<ProcessingOptions>() ?? new ProcessingOptions();` | Привязывает раздел FileProcessing к свойствам ProcessingOptions. Если раздел не дал объект, использует значения по умолчанию из класса. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:17>) | `options.Validate();` | Вызывает Validate: проверка параметров, нормализация путей, создание папок, пробная запись/удаление. При ошибке управление переходит в catch ниже. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:18>) | `builder.Services.AddSingleton(options);` | Регистрирует готовый объект настроек в DI-контейнере. Все получатели используют один экземпляр, а не читают JSON самостоятельно. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:19>) | `builder.Services.AddSingleton(logger);` | Регистрирует тот же FileLogger, который создан в строке 15. Worker получает его через конструктор. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:20>) | `builder.Services.AddWindowsService(o => o.ServiceName = "InternalIntegration");` | Подключает поддержку управления Windows Service с именем InternalIntegration. Не устанавливает службу в Windows; при консольном запуске Host остается консольным. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:21>) | `// .NET Host использует logging внутри себя. Отключаем все выводы, включая EventLog,` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:22>) | `// добавляемый AddWindowsService. Прикладной код пишет только через FileLogger.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:23>) | `builder.Logging.ClearProviders();` | Удаляет стандартные провайдеры журналирования после настройки Windows Service. Именно поэтому код с Microsoft.Extensions.Logging здесь есть, но ILogger для прикладного журнала не используется. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:24>) | `builder.Services.AddHostedService<Worker>();` | Регистрирует Worker как службу, которую Host должен запустить. Эта строка сама не вызывает ExecuteAsync. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:25>) | `using var host = builder.Build();` | Собирает Host. using гарантирует Dispose при выходе из блока, даже при исключении. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:26>) | `logger.Info("Конфигурация проверена");` | Пишет системное событие: file=-, requisitionID=не определён. Это еще не обработка заявки. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:27>) | `await host.StartAsync();` | Запускает Host; тот вызывает StartAsync зарегистрированных hosted services. BackgroundService запускает переопределенный Worker.ExecuteAsync. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:28>) | `await host.WaitForShutdownAsync();` | Ожидает Ctrl+C, остановку SCM или остановку Host после ошибки Worker; при завершении выполняет процедуру остановки служб. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:29>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:30>) | `catch (Exception error)` | Перехватывает исключение, вышедшее из создания конфигурации/Host или запуска. Ошибки фонового цикла также отдельно обрабатывает сам Worker. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:31>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [32](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:32>) | `Environment.ExitCode = 1;` | Назначает ненулевой код завершения процесса для администратора/SCM. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:33>) | `// Не выводим текст исключения, который может содержать XML пациента.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:34>) | `try { logger?.Error($"Приложение остановлено: {error.GetType().Name}"); }` | Пробует записать только имя типа исключения. ?. не вызывает Error, если логгер создать не удалось. Сам текст Exception.Message не раскрывается. |
| [35](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:35>) | `catch (FileLogException) { }` | Если недоступен сам журнал, не пытается бесконечно логировать ошибку записи журнала. |
| [36](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:36>) | `Console.Error.WriteLine($"Internal Integration остановлен: {error.GetType().Name}. Проверьте настройки и права доступа.");` | Пишет краткую аварийную диагностику в stderr. Это резервный вывод, а не второй полноценный журнал. |
| [37](<C:/Development/BAK lab/src/InternalIntegration/Program.cs:37>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |

### 9.2. Worker.cs — Фоновый цикл

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs>). Строк: 35. SHA-256: `5f6984b367cf8828dbc28fde74e363dbab1c0169afac49c65adb9e961a555cd4`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:1>) | `using Microsoft.Extensions.Hosting;` | Подключает Host и BackgroundService. Это импорт имен, не вызов функции. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:2>) | — | Пустая строка для читаемости; не выполняется. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:3>) | `namespace InternalIntegration;` | Объединяет классы файла в пространство имен InternalIntegration; каталог и процесс не создает. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:4>) | — | Пустая строка для читаемости; не выполняется. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:5>) | `/// <summary>Один последовательный цикл: сканирование, обработка готовых пар, пауза.</summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:6>) | `public sealed class Worker(ProcessingOptions options, FileLogger logger) : BackgroundService` | Класс фоновой службы. Primary constructor получает настройки и FileLogger из DI. sealed запрещает наследование от Worker; сам он наследует BackgroundService. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:7>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:8>) | `protected override async Task ExecuteAsync(CancellationToken stoppingToken)` | Метод вызывается инфраструктурой BackgroundService. async Task позволяет ожидать паузу и остановку; сам Scan внутри этого метода синхронный. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:9>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:10>) | `try` | Начинает защищенный блок: подходящие исключения из него обрабатываются в последующих catch. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:11>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:12>) | `using var engine = new PairEngine(options, logger);` | Создает один PairEngine на время работы Worker. using освобождает .processor.lock при выходе из try, включая ошибку и отмену. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:13>) | `logger.Info("Обработчик запущен");` | Пишет системное сообщение о запуске обработчика. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:14>) | `while (!stoppingToken.IsCancellationRequested)` | Повторяет обходы, пока Host не отменил stoppingToken. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:15>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:16>) | `// Первый обход сразу после запуска. Неготовая пара пропускается,` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:17>) | `// поэтому ожидание одного PDF не задерживает остальные заявки.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:18>) | `try { engine.Scan(DateTimeOffset.UtcNow, stoppingToken); }` | Передает текущее UTC-время и токен в Scan. Один вызов последовательно рассматривает все обнаруженные задания. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:19>) | `catch (Exception error) when (error is IOException or UnauthorizedAccessException)` | Перехватывает общие ошибки ввода-вывода/прав, которые не удалось обработать на уровне отдельной пары. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:20>) | `{ logger.Error($"Ошибка доступа при сканировании: {error.GetType().Name}"); }` | Записывает тип общей ошибки; после catch цикл дойдет до паузы и затем попробует сканировать снова. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:21>) | `await Task.Delay(TimeSpan.FromSeconds(options.ScanIntervalSeconds), stoppingToken);` | Асинхронно ждет заданную паузу ПОСЛЕ обхода. При 10 секундах интервал между началами обходов равен 10 секундам плюс длительность самого обхода. Отмена прерывает Delay. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:22>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:23>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:24>) | `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }` | Штатная отмена не считается ошибкой; пустой обработчик поглощает OperationCanceledException только при отмененном токене. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:25>) | `catch (Exception error)` | Все прочие ошибки, включая FileLogException, считаются фатальными для фонового обработчика. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:26>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:27>) | `// Host остановит службу при выходе исключения из BackgroundService.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:28>) | `Environment.ExitCode = 1;` | Делает итоговый код процесса ненулевым. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:29>) | `if (error is FileLogException) Console.Error.WriteLine("Недоступен файловый журнал. Обработка остановлена.");` | Если сломан журнал, пишет короткое сообщение в stderr, не вызывая неисправный логгер повторно. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:30>) | `else logger.Error($"Обработчик остановлен из-за ошибки: {error.GetType().Name}");` | Иначе записывает только тип фатальной ошибки в файл. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:31>) | `throw;` | Повторно выбрасывает исходное исключение с сохранением стека. Host обнаруживает сбой BackgroundService и по стандартному поведению останавливает приложение. |
| [32](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:32>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:33>) | `logger.Info("Обработчик остановлен");` | Сообщение штатной остановки: достигается при выходе из цикла или при отмене; при throw строки 31 не выполняется. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:34>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [35](<C:/Development/BAK lab/src/InternalIntegration/Worker.cs:35>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |

### 9.3. ProcessingOptions.cs — Настройки и проверка путей

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs>). Строк: 56. SHA-256: `c6834758bd73059f35ac7bc912425e0944073da052ad484537d9125dfae28348`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:1>) | `namespace InternalIntegration;` | Объединяет классы файла в пространство имен InternalIntegration; каталог и процесс не создает. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:2>) | — | Пустая строка для читаемости; не выполняется. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:3>) | `/// <summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:4>) | `/// Параметры раздела FileProcessing. Считываются один раз при старте.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:5>) | `/// Интервалы заданы в секундах, все рабочие пути после Validate становятся абсолютными.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:6>) | `/// </summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:7>) | `public sealed class ProcessingOptions` | Объект настроек. Значения свойств могут быть заменены конфигурацией при Get<ProcessingOptions>(). |
| [8](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:8>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:9>) | `// Три папки имеют разные роли: исходники, готовые результаты и журнал восстановления.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:10>) | `public string InputDirectory { get; set; } = "Input";` | Папка входа; значение Input используется только если конфигурация его не переопределила. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:11>) | `public string OutputDirectory { get; set; } = "Output";` | Папка итоговых файлов; внутри нее создается техническая .staging. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:12>) | `public string StateDirectory { get; set; } = "State";` | Папка JSON-состояния: дедлайн и этапы транзакций, а не текстовый журнал. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:13>) | `// Пауза между последовательными обходами; событийного наблюдателя в программе нет.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:14>) | `public int ScanIntervalSeconds { get; set; } = 10;` | Пауза между обходами: по умолчанию 10 секунд. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:15>) | `// Это минимальный интервал неизменности размера и LastWriteTime перед чтением.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:16>) | `public int FileStabilitySeconds { get; set; } = 3;` | Минимум стабильности размера/времени: 3 секунды. Это не отдельный таймер на каждый файл; проверка происходит при очередном обходе. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:17>) | `// Отсчитывается от сохраненного FirstReady, а не от каждого нового обхода.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:18>) | `public int PdfWaitTimeoutSeconds { get; set; } = 300;` | Таймаут отсутствующего PDF: 300 секунд от сохраненного FirstReady. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:19>) | `public int RetryDelaySeconds { get; set; } = 5;` | Задержка перед повторной попыткой после временной ошибки обработки: 5 секунд. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:20>) | `public int MaxRetryAttempts { get; set; } = 5;` | Максимум ошибок до блокировки задания: 5. Проверки еще записываемого файла этот счетчик не расходуют. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:21>) | — | Пустая строка для читаемости; не выполняется. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:22>) | `/// <summary>Проверяет конфигурацию, создает папки и проверяет права записи/удаления.</summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:23>) | `public void Validate()` | Публичная проверка конфигурации, вызывается Program до запуска Worker. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:24>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:25>) | `InputDirectory = Resolve(InputDirectory);` | Заменяет входной путь абсолютным через Resolve. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:26>) | `OutputDirectory = Resolve(OutputDirectory);` | Нормализует выходной путь. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:27>) | `StateDirectory = Resolve(StateDirectory);` | Нормализует путь состояния. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:28>) | `// Вложенные папки запрещены, чтобы собственные результаты/журналы не попадали во вход.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:29>) | `var paths = new[] { InputDirectory, OutputDirectory, StateDirectory };` | Собирает три пути для попарной проверки и создания папок. Путь Logs сюда не входит: им занимается FileLogger. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:30>) | `for (var i = 0; i < paths.Length; i++)` | Первый индекс пары каталогов. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:31>) | `for (var j = i + 1; j < paths.Length; j++)` | Второй индекс всегда больше первого; проверяются все разные пары без повторов. |
| [32](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:32>) | `if (Within(paths[i], paths[j]) \|\| Within(paths[j], paths[i]))` | Проверяет совпадение или вложенность в обоих направлениях. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:33>) | `throw new ArgumentException("Папки Input, Output и State должны быть раздельными и не вложенными.");` | Останавливает запуск при одинаковых/вложенных рабочих каталогах. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:34>) | `if (ScanIntervalSeconds <= 0 \|\| FileStabilitySeconds <= 0 \|\| PdfWaitTimeoutSeconds < 0 \|\|` | Проверяет интервалы: сканирование и стабильность должны быть положительными; таймаут PDF может равняться нулю. |
| [35](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:35>) | `RetryDelaySeconds <= 0 \|\| MaxRetryAttempts <= 0)` | Продолжает условие: задержка повторов и число попыток тоже должны быть положительными. |
| [36](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:36>) | `throw new ArgumentException("Интервалы и число попыток должны быть положительными; таймаут PDF может быть нулевым.");` | Выбрасывает ошибку конфигурации, если хотя бы одно условие нарушено. |
| [37](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:37>) | `foreach (var path in paths)` | Поочередно проверяет каждую рабочую папку. |
| [38](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:38>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [39](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:39>) | `Directory.CreateDirectory(path);` | Создает папку и ее отсутствующие родители; существующую папку не очищает. |
| [40](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:40>) | `if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)` | Проверяет флаг ReparsePoint у самой папки: это может быть junction/ссылка. |
| [41](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:41>) | `throw new ArgumentException("Папки-ссылки не поддерживаются.");` | Запрещает такие папки. Это не полная проверка всех возможных алиасов в родительских путях. |
| [42](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:42>) | `// Проверка только существования папки недостаточна: учетной записи службы` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [43](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:43>) | `// понадобятся права создавать и удалять файлы на всех этапах транзакции.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [44](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:44>) | `var probe = Path.Combine(path, $".probe-{Guid.NewGuid():N}");` | Создает уникальное имя пробного файла. |
| [45](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:45>) | `using (File.Create(probe)) { }` | Создает пустой файл и сразу закрывает дескриптор благодаря using. |
| [46](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:46>) | `File.Delete(probe);` | Удаляет пробный файл; проверяются и запись, и удаление. |
| [47](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:47>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [48](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:48>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [49](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:49>) | `// AppContext.BaseDirectory стабилен и при F5, и при запуске через Windows SCM.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [50](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:50>) | `private static string Resolve(string path) => string.IsNullOrWhiteSpace(path)` | Начало короткого метода Resolve: сначала проверка пустой строки. |
| [51](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:51>) | `? throw new ArgumentException("Путь не может быть пустым.")` | Первая ветвь условного оператора: пустой путь вызывает исключение. |
| [52](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:52>) | `: Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, AppContext.BaseDirectory));` | Вторая ветвь: получает абсолютный путь от AppContext.BaseDirectory и убирает лишний конечный разделитель (корень сохраняется). |
| [53](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:53>) | `// Учитываем разделитель: C:\\Input2 не является дочерней папкой C:\\Input.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [54](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:54>) | `private static bool Within(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) \|\|` | Начало Within: совпадение без учета регистра уже считается попаданием в один каталог. |
| [55](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:55>) | `a.StartsWith(Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);` | Либо путь a начинается с b и разделителя. Так Input2 не считается вложенным в Input. Обрабатывается также корень диска. |
| [56](<C:/Development/BAK lab/src/InternalIntegration/ProcessingOptions.cs:56>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |

### 9.4. PairEngine.cs — Движок пары

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs>). Строк: 253. SHA-256: `504669291369056f7e2eadacc413ef7cea5b7fc6d8ad244cc84d522e131c66e3`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:1>) | `using System.Security.Cryptography;` | Подключает SHA256 для контрольных сумм. Это импорт имен, не вызов функции. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:2>) | `using System.Text.Json;` | Подключает сериализацию JSON и JsonException. Это импорт имен, не вызов функции. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:3>) | `using System.Xml;` | Подключает безопасный XML reader/writer, настройки и XmlException. Это импорт имен, не вызов функции. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:4>) | — | Пустая строка для читаемости; не выполняется. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:5>) | `namespace InternalIntegration;` | Объединяет классы файла в пространство имен InternalIntegration; каталог и процесс не создает. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:6>) | — | Пустая строка для читаемости; не выполняется. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:7>) | `/// <summary>Последовательная обработка: готовность → подготовка → публикация → удаление исходников.</summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:8>) | `public sealed class PairEngine : IDisposable` | Движок обработки. IDisposable нужен для освобождения блокировки входной папки. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:9>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:10>) | `private readonly ProcessingOptions options;` | Общие настройки рабочих каталогов и интервалов. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:11>) | `private readonly FileLogger logger;` | Общий прямой файловый логгер. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:12>) | `private readonly StateStore store;` | Объект чтения/сохранения состояния заданий. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:13>) | `private readonly FileStream instanceLock;` | Открытый поток .processor.lock держит межпроцессную блокировку. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:14>) | `private readonly Dictionary<string, (long Size, DateTime Write, DateTimeOffset Since)> observations = new(StringComparer.OrdinalIgnoreCase);` | Память о наблюдениях: для каждого пути хранит размер, время изменения и момент начала стабильности. После перезапуска собирается заново. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:15>) | `private bool corruptStateReported;` | Не позволяет повторять одно и то же сообщение о повреждении состояния при каждом обходе. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:16>) | — | Пустая строка для читаемости; не выполняется. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:17>) | `// Только для тестов восстановления: имитирует остановку между этапами записи на диск.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:18>) | `public Action<string>? Checkpoint { get; set; }` | Необязательный callback тестов. В обычной службе null; ?.Invoke ничего не делает. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:19>) | — | Пустая строка для читаемости; не выполняется. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:20>) | `public PairEngine(ProcessingOptions options, FileLogger logger)` | Конструктор получает зависимости; вызывается один раз из Worker. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:21>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:22>) | `this.options = options;` | Сохраняет настройки. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:23>) | `this.logger = logger;` | Сохраняет логгер. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:24>) | `store = new StateStore(options.StateDirectory);` | Создает хранилище состояния в StateDirectory. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:25>) | `// Файловая блокировка не допускает второй процесс с той же входной папкой.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:26>) | `instanceLock = new FileStream(Path.Combine(options.InputDirectory, ".processor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);` | Создает/открывает .processor.lock с FileShare.None. Другой процесс не сможет открыть этот же файл до Dispose; наличие файла без открытого дескриптора само по себе не блокирует запуск. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:27>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:28>) | — | Пустая строка для читаемости; не выполняется. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:29>) | `/// <summary>Один обход; вызывается только последовательно из Worker.</summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:30>) | `public void Scan(DateTimeOffset now, CancellationToken token)` | Один синхронный обход с переданным UTC-временем и токеном. Метод не рассчитан на параллельные вызовы. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:31>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [32](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:32>) | `token.ThrowIfCancellationRequested();` | Быстро выходит исключением отмены, если запрошена остановка. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:33>) | `Dictionary<string, PairState> states;` | Объявляет словарь заданий, который далее заполнится из JSON. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:34>) | `try { states = store.Load(); corruptStateReported = false; }` | Загружает состояния; при успешной загрузке снимает флаг ранее сообщенной общей ошибки. |
| [35](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:35>) | `catch (Exception error) when (error is JsonException or ArgumentException)` | Перехватывает поврежденный JSON/некорректные значения, которые нельзя безопасно продолжить. |
| [36](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:36>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [37](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:37>) | `// Без достоверного состояния нельзя отличить новую пару от прерванной публикации.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [38](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:38>) | `if (!corruptStateReported) logger.Error("Поврежден журнал состояния; обработка приостановлена");` | Один раз пишет общую ошибку состояния; номер заявки может быть неизвестен. |
| [39](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:39>) | `corruptStateReported = true;` | Запоминает, что ошибка уже сообщена. |
| [40](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:40>) | `return;` | Завершает текущий обход целиком; следующие файлы не обрабатываются при ненадежном журнале. |
| [41](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:41>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [42](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:42>) | — | Пустая строка для читаемости; не выполняется. |
| [43](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:43>) | `// Получаем список ОДИН раз за обход, вместо отдельного поиска PDF для каждого XML.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [44](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:44>) | `var files = Directory.EnumerateFiles(options.InputDirectory)` | Получает перечисление всех файлов непосредственно входной папки, без подпапок. |
| [45](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:45>) | `.ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);` | Материализует словарь имя→полный путь без учета регистра. Дальнейшие поиски PDF идут в этом снимке, без повторного обхода папки. |
| [46](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:46>) | `foreach (var name in files.Keys.Where(n => Path.GetExtension(n).Equals(".xml", StringComparison.OrdinalIgnoreCase)))` | Выбирает имена с расширением .xml; .XML тоже подходит. |
| [47](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:47>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [48](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:48>) | `token.ThrowIfCancellationRequested();` | Проверяет остановку перед следующим XML. |
| [49](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:49>) | `if (states.ContainsKey(name) \|\| !Ready(files[name], now)) continue;` | Уже известный XML не регистрируется заново. Для нового вызывается Ready; если он еще нестабилен/недоступен, идет следующий XML. |
| [50](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:50>) | `var state = new PairState { Name = name, FirstReady = now };` | Создает задание с FirstReady=now. Здесь содержимое XML еще не разбиралось: номера заявки пока нет. |
| [51](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:51>) | `store.Save(state); // С этого момента перезапуск не сбрасывает дедлайн PDF.` | Сохраняет первое время готовности на диск, чтобы перезапуск не начал ожидание PDF заново. |
| [52](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:52>) | `states.Add(name, state);` | Добавляет задание в память этого обхода; оно будет рассмотрено в следующем foreach того же Scan. |
| [53](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:53>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [54](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:54>) | — | Пустая строка для читаемости; не выполняется. |
| [55](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:55>) | `foreach (var state in states.Values)` | Обходит все задания, включая восстановленные и созданные только что. |
| [56](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:56>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [57](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:57>) | `token.ThrowIfCancellationRequested();` | Проверяет отмену. |
| [58](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:58>) | `if (state.Blocked \|\| state.RetryAfter > now) continue;` | Пропускает заблокированные задания и те, у которых еще не наступила новая попытка. |
| [59](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:59>) | `try { Process(state, files, now, token); }` | Вызывает Process для одной пары. |
| [60](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:60>) | `catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)` | Перехватывает ожидаемые ошибки пары. FileLogException и отмена в этот список не входят: они не должны превращаться в обычные повторные попытки. |
| [61](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:61>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [62](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:62>) | `state.Failures++;` | Увеличивает количество ошибок задания. |
| [63](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:63>) | `state.RetryAfter = now.AddSeconds(options.RetryDelaySeconds);` | Назначает минимальное время следующей попытки. Оно проверяется при очередном сканировании, не отдельным таймером. |
| [64](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:64>) | `state.Blocked = error is ConflictException or XmlException or ArgumentException or NotSupportedException \|\|` | Постоянные ошибки (конфликт, XML, аргументы, неподдерживаемый формат) блокируют задание сразу. |
| [65](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:65>) | `state.Failures >= options.MaxRetryAttempts;` | Временные ошибки тоже блокируют задание после MaxRetryAttempts. |
| [66](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:66>) | `store.Save(state);` | Записывает счетчик, дедлайн и блокировку в JSON, чтобы состояние пережило перезапуск. |
| [67](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:67>) | `// Логируем тип ошибки, но не Message парсера, содержащий части XML.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [68](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:68>) | `Log(state, $"{(state.Blocked ? "Окончательная" : "Временная")} ошибка: {error.GetType().Name}; попытка={state.Failures}; исходники не удаляются", true);` | Пишет ошибку с файлом и известными номерами заявок. Для исходного примера с DTD номер еще неизвестен, Failures=1, Blocked=true. |
| [69](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:69>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [70](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:70>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [71](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:71>) | `// Не накапливаем сведения о давно удаленных входных файлах.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [72](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:72>) | `foreach (var path in observations.Keys.Where(p => !files.ContainsKey(Path.GetFileName(p))).ToArray())` | Собирает отдельный массив путей наблюдений, которых больше нет во входном снимке; ToArray позволяет затем менять словарь без ошибки перечисления. |
| [73](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:73>) | `observations.Remove(path);` | Удаляет устаревшее наблюдение из памяти. После удаления исходника в этом же обходе запись может исчезнуть только при следующем обходе. |
| [74](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:74>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [75](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:75>) | — | Пустая строка для читаемости; не выполняется. |
| [76](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:76>) | `private void Process(PairState state, Dictionary<string, string> files, DateTimeOffset now, CancellationToken token)` | Оркестратор обработки одной пары: выбирает нужный этап по состоянию. |
| [77](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:77>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [78](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:78>) | `var xml = Path.Combine(options.InputDirectory, state.Name);` | Строит полный путь XML. |
| [79](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:79>) | `var expectedPdf = state.PdfName ?? Path.ChangeExtension(state.Name, ".pdf");` | Берет сохраненное имя PDF или меняет расширение имени XML на .pdf. |
| [80](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:80>) | `var pdf = files.GetValueOrDefault(expectedPdf) ?? Path.Combine(options.InputDirectory, expectedPdf);` | Использует путь PDF из снимка папки (с фактическим регистром) либо предполагаемый путь для проверки отсутствия. |
| [81](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:81>) | `if (state.Transaction == null)` | Если транзакция еще не подготовлена, нужно дождаться файлов и выполнить преобразование. |
| [82](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:82>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [83](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:83>) | `if (!CheckFilesReady(state, xml, pdf, now)) return;` | CheckFilesReady=false завершает обработку только этой пары, не всего обхода. |
| [84](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:84>) | `token.ThrowIfCancellationRequested();` | Не начинает подготовку при отмененной службе. |
| [85](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:85>) | `PrepareFiles(state, xml, pdf);` | Вызывает PrepareFiles: XML, временные результаты и сохраненное состояние. |
| [86](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:86>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [87](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:87>) | `token.ThrowIfCancellationRequested();` | Проверяет остановку перед публикацией; подготовленные файлы уже можно восстановить после перезапуска. |
| [88](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:88>) | `PublishFiles(state);` | Публикует PDF, затем XML, либо подтверждает уже выполненную публикацию. |
| [89](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:89>) | `// Даже после Published повторяем проверку перед удалением любого исходника.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [90](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:90>) | `Verify(Destination(state.Name), state.XmlHash!, state.XmlIdentity);` | Проверяет итоговый XML по хешу и идентификатору NTFS. |
| [91](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:91>) | `if (state.PdfHash != null) Verify(Destination(state.PdfName ?? Path.ChangeExtension(state.Name, ".pdf")), state.PdfHash, state.PdfIdentity);` | Если PDF входит в транзакцию, проверяет и его. Для XML без PDF проверка не выполняется. |
| [92](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:92>) | `LogResults(state);` | Записывает подробный результат только после проверки выходных файлов. |
| [93](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:93>) | `token.ThrowIfCancellationRequested();` | Последняя проверка отмены перед удалением исходников. |
| [94](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:94>) | `DeleteSources(state, xml, pdf);` | Удаляет исходные XML/PDF после проверки их хешей. |
| [95](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:95>) | `Log(state, "Обработка завершена; исходники удалены");` | Пишет сообщение об окончании удаления исходников; техническая очистка еще впереди. |
| [96](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:96>) | `// Журнал удаляется последним: сбой очистки всегда можно продолжить.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [97](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:97>) | `if (Directory.Exists(Stage(state))) Directory.Delete(Stage(state), true);` | Удаляет уникальную staging-папку этой транзакции, если она осталась. |
| [98](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:98>) | `store.Delete(state);` | Удаляет JSON последним. До этой строки сбой можно продолжить по сохраненному Published. |
| [99](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:99>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [100](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:100>) | — | Пустая строка для читаемости; не выполняется. |
| [101](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:101>) | `private bool CheckFilesReady(PairState state, string xml, string pdf, DateTimeOffset now)` | Определяет готовность пары без блокирующего ожидания. |
| [102](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:102>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [103](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:103>) | `if (File.Exists(pdf) && !state.PdfSeen)` | Если PDF существует и еще не был замечен, фиксирует его появление. |
| [104](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:104>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [105](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:105>) | `state.PdfSeen = true;` | Защелкивает PdfSeen=true. |
| [106](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:106>) | `state.PdfName = Path.GetFileName(pdf); // Сохраняем в том числе регистр расширения.` | Сохраняет имя с исходным регистром расширения. |
| [107](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:107>) | `store.Save(state);` | Фиксирует PdfSeen/PdfName на диске. |
| [108](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:108>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [109](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:109>) | `if (!Ready(xml, now)) return false;` | Проверяет готовность XML повторно, в том числе после перезапуска. |
| [110](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:110>) | `// Уже замеченный PDF ожидаем даже после дедлайна. Отсутствующий PDF - до таймаута.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [111](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:111>) | `return state.PdfSeen ? Ready(pdf, now) : now - state.FirstReady >= TimeSpan.FromSeconds(options.PdfWaitTimeoutSeconds);` | Если PDF замечен, требует Ready(pdf). Иначе разрешает XML без PDF только когда now−FirstReady не меньше PdfWaitTimeoutSeconds. |
| [112](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:112>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [113](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:113>) | — | Пустая строка для читаемости; не выполняется. |
| [114](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:114>) | `private void PrepareFiles(PairState state, string xml, string pdf)` | Готовит результаты; итоговые файлы еще не появляются, исходники не удаляются. |
| [115](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:115>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [116](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:116>) | `// Блокируем запись в исходники на все время чтения и подготовки результата.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [117](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:117>) | `using var xmlStream = OpenSource(xml);` | Открывает XML через OpenSource с запретом записи/удаления другими процессами. |
| [118](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:118>) | `using var pdfStream = state.PdfSeen ? OpenSource(pdf) : null;` | Открывает PDF на тех же условиях, если он участвует в паре; иначе сохраняет null. |
| [119](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:119>) | `using var buffer = new MemoryStream();` | Создает буфер XML в памяти. |
| [120](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:120>) | `xmlStream.CopyTo(buffer);` | Считывает весь XML в буфер. |
| [121](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:121>) | `var source = buffer.ToArray();` | Получает byte[] исходного XML. |
| [122](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:122>) | `var result = new XmlTransformer().Transform(source);` | Вызывает XmlTransformer.Transform. Если здесь исключение, строки 123 и далее не выполнятся. |
| [123](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:123>) | `state.RequisitionIds = result.RequisitionIds;` | Только после успешного Transform появляются известные номера заявок. |
| [124](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:124>) | `state.Results = result.Results;` | Сохраняет подробные результаты для будущих логов и восстановления. |
| [125](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:125>) | `state.Replacements = result.Replacements;` | Сохраняет общее количество замен. |
| [126](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:126>) | `Log(state, "Начало обработки");` | Пишет «Начало обработки». В текущей реализации это ПОСЛЕ успешного разбора/преобразования в памяти, а не до чтения XML. |
| [127](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:127>) | `if (result.AmbiguousTests > 0) Log(state, $"Неоднозначная принадлежность тестов: {result.AmbiguousTests}; для них замена не выполняется");` | При неоднозначных тестах пишет их количество; сами тесты остаются без замены. |
| [128](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:128>) | `if (File.Exists(Destination(state.Name)) \|\| File.Exists(Destination(Path.GetFileName(pdf)))) throw new ConflictException();` | Проверяет отсутствие обоих итоговых имен. Даже существующий PDF при обработке XML без PDF считается конфликтом. Исходники остаются. |
| [129](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:129>) | — | Пустая строка для читаемости; не выполняется. |
| [130](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:130>) | `var transaction = Guid.NewGuid().ToString("N");` | Создает случайный идентификатор транзакции в формате N. |
| [131](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:131>) | `var staging = Path.Combine(options.OutputDirectory, ".staging", transaction);` | Строит путь Output/.staging/GUID на том же выходном томе. |
| [132](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:132>) | `Directory.CreateDirectory(staging);` | Создает эту папку. |
| [133](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:133>) | `StateStore.WriteDurable(Path.Combine(staging, "xml"), result.Bytes);` | Полностью пишет выходные байты XML во временный файл xml без расширения. |
| [134](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:134>) | `string? pdfHash = null;` | По умолчанию PDF не участвует, его хеш null. |
| [135](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:135>) | `if (pdfStream != null)` | Проверяет, был ли открыт поток PDF. |
| [136](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:136>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [137](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:137>) | `// PDF копируем потоком. Ни разбора, ни генерации, ни модификации PDF здесь нет.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [138](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:138>) | `using var output = new FileStream(Path.Combine(staging, "pdf"), FileMode.CreateNew, FileAccess.Write, FileShare.None);` | Создает новый временный pdf. CreateNew запрещает перезапись даже внутри уникальной staging-папки. |
| [139](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:139>) | `pdfStream.CopyTo(output);` | Копирует PDF как поток байтов без обработки его формата. |
| [140](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:140>) | `output.Flush(true);` | Сбрасывает PDF на диск. |
| [141](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:141>) | `pdfStream.Position = 0;` | Возвращает позицию исходного PDF к началу, потому что CopyTo дочитал до конца. |
| [142](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:142>) | `pdfHash = Convert.ToHexString(SHA256.HashData(pdfStream));` | Вычисляет SHA-256 всего исходного PDF, а не пустого остатка потока. |
| [143](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:143>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [144](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:144>) | `state.SourceXmlHash = Convert.ToHexString(SHA256.HashData(source));` | Считает хеш исходного XML для безопасного удаления. |
| [145](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:145>) | `state.XmlHash = Convert.ToHexString(SHA256.HashData(result.Bytes));` | Считает хеш выходных байтов XML для контроля публикации. |
| [146](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:146>) | `state.PdfHash = pdfHash;` | Запоминает хеш PDF или null. |
| [147](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:147>) | `state.XmlIdentity = FileIdentity.Read(Path.Combine(staging, "xml"));` | Запоминает идентификатор NTFS подготовленного XML. |
| [148](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:148>) | `state.PdfIdentity = pdfHash == null ? null : FileIdentity.Read(Path.Combine(staging, "pdf"));` | Запоминает аналогичный идентификатор PDF, если он есть. |
| [149](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:149>) | `// Флаг готовности ставим последним, когда оба временных файла полностью записаны.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [150](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:150>) | `state.Transaction = transaction;` | Помечает транзакцию подготовленной только после успешной записи всех нужных данных. |
| [151](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:151>) | `store.Save(state);` | Фиксирует всю подготовку в JSON. |
| [152](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:152>) | `Checkpoint?.Invoke("Prepared");` | Позволяет тесту прервать выполнение сразу после фиксации Prepared. |
| [153](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:153>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [154](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:154>) | — | Пустая строка для читаемости; не выполняется. |
| [155](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:155>) | `private void PublishFiles(PairState state)` | Управляет порядком публикации пары. |
| [156](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:156>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [157](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:157>) | `if (state.Published) return;` | При Published=true повторное перемещение не требуется; Process все равно перепроверит итоговые файлы. |
| [158](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:158>) | `if (state.PdfHash != null)` | Публикует PDF только если его хеш сохранен. |
| [159](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:159>) | `PublishOne(state, "pdf", state.PdfName ?? Path.ChangeExtension(state.Name, ".pdf"), state.PdfHash, state.PdfIdentity, state.PdfIntent);` | Вызывает PublishOne для PDF. |
| [160](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:160>) | `Checkpoint?.Invoke("AfterPdf");` | Тестовая точка после этапа PDF. Срабатывает и в XML-only сценарии, где PDF не было. |
| [161](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:161>) | `PublishOne(state, "xml", state.Name, state.XmlHash!, state.XmlIdentity, state.XmlIntent);` | Публикует XML последним: это сигнал готовности для потребителя. |
| [162](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:162>) | `state.Published = true;` | Помечает завершение публикации в памяти. |
| [163](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:163>) | `store.Save(state);` | Сохраняет Published на диск. |
| [164](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:164>) | `Checkpoint?.Invoke("Published");` | Тестовая точка после сохранения Published. |
| [165](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:165>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [166](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:166>) | — | Пустая строка для читаемости; не выполняется. |
| [167](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:167>) | `private void PublishOne(PairState state, string kind, string name, string hash, string? identity, bool intent)` | Публикует один файл с учетом сохраненного намерения и возможного прошлого сбоя. |
| [168](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:168>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [169](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:169>) | `var destination = Destination(name);` | Формирует его итоговый путь. |
| [170](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:170>) | `if (File.Exists(destination))` | Если по этому пути уже есть файл, проверяет сценарий восстановления. |
| [171](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:171>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [172](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:172>) | `// Для продолжения требуем свое намерение, совпадение байтов и объекта NTFS.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [173](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:173>) | `// Чужой файл с теми же байтами все равно является конфликтом.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [174](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:174>) | `if (!intent) throw new ConflictException();` | Если собственного намерения не было, любой существующий файл является конфликтом. |
| [175](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:175>) | `Verify(destination, hash, identity);` | При намерении проверяет байты и идентичность файлового объекта; несовпадение тоже конфликт. |
| [176](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:176>) | `return;` | Уже корректно опубликованный файл больше не перемещает. |
| [177](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:177>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [178](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:178>) | `var stage = Path.Combine(Stage(state), kind);` | Находит временный xml или pdf в каталоге транзакции. |
| [179](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:179>) | `Verify(stage, hash, identity);` | Проверяет временный файл перед переименованием. |
| [180](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:180>) | `if (kind == "pdf") state.PdfIntent = true; else state.XmlIntent = true;` | Устанавливает флаг намерения именно для публикуемого типа. |
| [181](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:181>) | `store.Save(state); // Сначала намерение на диск, только затем переименование.` | Сохраняет намерение ДО File.Move; так сбой после перемещения распознается при восстановлении. |
| [182](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:182>) | `Checkpoint?.Invoke(kind + "Intent");` | Тестовая точка pdfIntent или xmlIntent. |
| [183](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:183>) | `File.Move(stage, destination, false);` | Переименовывает временный файл в итоговый без перезаписи (false). Это операция над одним файлом, не атомарное перемещение всей пары. |
| [184](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:184>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [185](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:185>) | — | Пустая строка для читаемости; не выполняется. |
| [186](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:186>) | `private void LogResults(PairState state)` | Пишет результаты по заявкам из сохраненного состояния. |
| [187](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:187>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [188](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:188>) | `// Детали сохраняются в состоянии: после прерывания до лога они не теряются.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [189](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:189>) | `// При сбое после записи лога возможен повтор записи результата при восстановлении.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [190](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:190>) | `foreach (var result in state.Results)` | Обходит все результаты текущего XML. |
| [191](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:191>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [192](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:192>) | `if (result.Changes.Length == 0) logger.Info($"Без изменений: {result.Reason}", state.Name, result.RequisitionId);` | Если массив Changes пуст, выводит причину отсутствия изменений для данной заявки. |
| [193](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:193>) | `foreach (var change in result.Changes)` | Иначе/дополнительно обходит изменения; для пустого массива тело не выполняется. |
| [194](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:194>) | `logger.Info($"Изменение опубликовано: LaboratoryID={change.LaboratoryId}; {change.OldCode} -> {change.NewCode}; количество={change.Count}", state.Name, result.RequisitionId);` | Записывает лабораторию, старый/новый код и количество, а также исходное имя XML и RequisitionId этой заявки. |
| [195](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:195>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [196](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:196>) | `// Старые журналы не содержали деталей замен: не выдумываем их при миграции.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [197](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:197>) | `if (state.Results.Length == 0)` | Обрабатывает старые JSON, у которых Results отсутствовал, либо XML без распознанных заявок. |
| [198](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:198>) | `Log(state, state.Replacements == 0 ? "Без изменений" : $"Опубликован ранее подготовленный XML; замен={state.Replacements}; детали отсутствуют в старом состоянии");` | Выводит либо отсутствие изменений, либо общее число старых замен без выдумывания отсутствующих деталей. |
| [199](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:199>) | `Log(state, state.PdfHash == null ? "XML опубликован без PDF: срок ожидания истек" : "Пара XML/PDF опубликована");` | Отдельно отмечает публикацию без PDF по таймауту или публикацию пары. |
| [200](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:200>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [201](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:201>) | — | Пустая строка для читаемости; не выполняется. |
| [202](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:202>) | `private void DeleteSources(PairState state, string xml, string pdf)` | Удаляет исходники только после успешной публикации/проверки в Process. |
| [203](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:203>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [204](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:204>) | `DeleteVerified(xml, state.SourceXmlHash!);` | Удаляет XML через безопасную проверку его исходного хеша. |
| [205](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:205>) | `Checkpoint?.Invoke("XmlDeleted");` | Тестовая точка между удалением XML и PDF. |
| [206](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:206>) | `if (state.PdfHash != null) DeleteVerified(pdf, state.PdfHash);` | Удаляет исходный PDF только если он входил в транзакцию. Поздний PDF после XML-only обработки не удаляет. |
| [207](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:207>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [208](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:208>) | — | Пустая строка для читаемости; не выполняется. |
| [209](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:209>) | `private bool Ready(string path, DateTimeOffset now)` | Проверяет готовность файла; не содержит ожидания или Sleep. |
| [210](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:210>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [211](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:211>) | `try` | Начинает защищенный блок: подходящие исключения из него обрабатываются в последующих catch. |
| [212](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:212>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [213](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:213>) | `var info = new FileInfo(path);` | Получает файловые метаданные. |
| [214](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:214>) | `if (!info.Exists) { observations.Remove(path); return false; }` | Если файла нет, удаляет старое наблюдение и сообщает «не готов». |
| [215](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:215>) | `if (!observations.TryGetValue(path, out var old) \|\| old.Size != info.Length \|\| old.Write != info.LastWriteTimeUtc)` | Проверяет, было ли наблюдение и совпадают ли размер и LastWriteTimeUtc. |
| [216](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:216>) | `{ observations[path] = (info.Length, info.LastWriteTimeUtc, now); return false; }` | При первом наблюдении/изменении сохраняет новую подпись с Since=now и возвращает false. |
| [217](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:217>) | `if (now - old.Since < TimeSpan.FromSeconds(options.FileStabilitySeconds)) return false;` | Даже неизменный файл не считается готовым до истечения FileStabilitySeconds от Since. |
| [218](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:218>) | `using var stream = OpenSource(path);` | Пробует открыть файл без возможности параллельной записи. |
| [219](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:219>) | `return stream.Length == old.Size && File.GetLastWriteTimeUtc(path) == old.Write;` | Повторно проверяет размер открытого файла и время: они должны совпасть с наблюдением. |
| [220](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:220>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [221](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:221>) | `// Еще поступающий файл пропускаем; следующий обход повторит проверку.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [222](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:222>) | `catch (IOException) { return false; }` | Ошибка открытия/ввода-вывода пока считается незавершенной записью; следующий обход попробует снова. |
| [223](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:223>) | `catch (UnauthorizedAccessException) { return false; }` | При отсутствии прав также возвращает false. Это может означать длительное молчаливое ожидание, а не расход MaxRetryAttempts. |
| [224](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:224>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [225](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:225>) | — | Пустая строка для читаемости; не выполняется. |
| [226](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:226>) | `private void Log(PairState state, string message, bool error = false)` | Помощник записи одного общего события отдельно для каждой известной заявки. |
| [227](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:227>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [228](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:228>) | `foreach (var id in state.RequisitionIds.Length == 0 ? ["не определён"] : state.RequisitionIds)` | Если номера еще неизвестны, использует один элемент «не определён»; иначе берет сохраненные номера. |
| [229](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:229>) | `if (error) logger.Error(message, state.Name, id); else logger.Info(message, state.Name, id);` | Вызывает Error или Info с именем XML и соответствующим номером заявки. |
| [230](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:230>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [231](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:231>) | — | Пустая строка для читаемости; не выполняется. |
| [232](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:232>) | `private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);` | Общее открытие исходника: только чтение; другие читатели разрешены, писатели и удаление запрещены на время потока. |
| [233](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:233>) | `private string Destination(string name) => Path.Combine(options.OutputDirectory, name);` | Соединяет OutputDirectory и итоговое имя. |
| [234](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:234>) | `private string Stage(PairState state) => Path.Combine(options.OutputDirectory, ".staging", state.Transaction!);` | Строит staging-путь по GUID из состояния. ! выражает ожидание, что подготовка уже была. |
| [235](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:235>) | — | Пустая строка для читаемости; не выполняется. |
| [236](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:236>) | `private static void Verify(string path, string hash, string? identity)` | Проверка содержимого и происхождения файла. |
| [237](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:237>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [238](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:238>) | `using var stream = OpenSource(path);` | Открывает файл и удерживает запрет изменения во время вычисления хеша. |
| [239](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:239>) | `if (Convert.ToHexString(SHA256.HashData(stream)) != hash \|\| FileIdentity.Read(path) != identity) throw new ConflictException();` | Любое несовпадение SHA-256 ИЛИ идентификатора NTFS вызывает ConflictException. |
| [240](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:240>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [241](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:241>) | — | Пустая строка для читаемости; не выполняется. |
| [242](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:242>) | `private static void DeleteVerified(string path, string hash)` | Проверка перед удалением одного исходника. |
| [243](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:243>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [244](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:244>) | `if (!File.Exists(path)) return; // Возможно, удаление уже произошло до перезапуска.` | Отсутствующий исходник считается уже удаленным ранее; это нужно для восстановления после частичной очистки. |
| [245](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:245>) | `using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);` | Открывает на чтение, запрещая запись, но разрешая удаление открытого файла (FileShare.Delete). |
| [246](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:246>) | `if (Convert.ToHexString(SHA256.HashData(stream)) != hash) throw new ConflictException();` | Сравнивает текущие байты с сохраненным исходным хешем. Если поставщик успел подменить содержимое, удаление не выполняется. |
| [247](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:247>) | `File.Delete(path);` | Удаляет исходный файл; открытый поток будет освобожден при выходе из метода. |
| [248](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:248>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [249](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:249>) | — | Пустая строка для читаемости; не выполняется. |
| [250](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:250>) | `public void Dispose() => instanceLock.Dispose();` | Закрывает .processor.lock; после этого другой процесс может работать с входной папкой. |
| [251](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:251>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [252](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:252>) | — | Пустая строка для читаемости; не выполняется. |
| [253](<C:/Development/BAK lab/src/InternalIntegration/PairEngine.cs:253>) | `public sealed class ConflictException : IOException;` | Отдельный тип конфликта, наследующий IOException. Scan отличает его от временной ошибки и сразу блокирует задание. |

### 9.5. XmlTransformer.cs — Разбор XML и замены

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs>). Строк: 93. SHA-256: `da74f3d5ad655ef6a47b2156b0d7af07a60b03667fd6d2acabc065699d0502e2`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:1>) | `using System.Text;` | Подключает Encoding и другие средства текста/кодировок. Это импорт имен, не вызов функции. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:2>) | `using System.Xml;` | Подключает безопасный XML reader/writer, настройки и XmlException. Это импорт имен, не вызов функции. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:3>) | `using System.Xml.Linq;` | Подключает XDocument/XElement и LINQ to XML. Это импорт имен, не вызов функции. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:4>) | — | Пустая строка для читаемости; не выполняется. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:5>) | `namespace InternalIntegration;` | Объединяет классы файла в пространство имен InternalIntegration; каталог и процесс не создает. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:6>) | — | Пустая строка для читаемости; не выполняется. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:7>) | `public sealed record TestChange(string LaboratoryId, string OldCode, string NewCode, int Count);` | Неизменяемая запись об одном виде замены: лаборатория, старый/новый код, количество одинаковых замен. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:8>) | `public sealed record RequisitionResult(string RequisitionId, string Reason, TestChange[] Changes);` | Результат одной заявки: номер, причина отсутствия изменений и массив замен. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:9>) | `public sealed record TransformResult(byte[] Bytes, int Replacements, string[] RequisitionIds, int AmbiguousTests, RequisitionResult[] Results);` | Результат всего документа: выходные байты, суммарные замены, номера заявок, неоднозначные тесты и подробные результаты. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:10>) | — | Пустая строка для читаемости; не выполняется. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:11>) | `public sealed class XmlTransformer` | Класс чистой XML-обработки: не перемещает файлы и не пишет журнал. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:12>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:13>) | `public TransformResult Transform(byte[] source)` | Принимает байты XML и возвращает TransformResult либо выбрасывает ошибку разбора. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:14>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:15>) | `// Поддерживаем кодировки Windows; DTD и внешние сущности запрещены.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:16>) | `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);` | Включает поддержку старых кодовых страниц, например windows-1251. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:17>) | `using var input = new MemoryStream(source);` | Открывает массив байтов как поток для XmlReader. using закроет поток. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:18>) | `using var reader = XmlReader.Create(input, new XmlReaderSettings` | Создает потоковый XML-парсер с явно заданными ограничениями. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:19>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:20>) | `DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,` | DTD запрещены полностью, а XmlResolver отключен. Наличие DOCTYPE в исходном примере приведет к ошибке при чтении, даже если файл DTD не открывается. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:21>) | `MaxCharactersInDocument = 64 * 1024 * 1024` | Ограничивает разбор 64 × 1024 × 1024 символами. Это лимит символов парсера, не байтов исходного файла в памяти. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:22>) | `});` | Завершает инициализатор аргумента и вызов метода. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:23>) | `var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);` | Читает весь документ в XDocument с сохранением пробельных узлов. Именно здесь в текущем примере возникает XmlException из-за DOCTYPE. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:24>) | `var root = document.Root ?? throw new XmlException("Отсутствует корневой элемент.");` | Извлекает корневой элемент или сообщает, что его нет. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:25>) | `if (root.Name.LocalName != "SafirMessage") throw new XmlException("Неизвестная структура XML.");` | Ожидает корень с локальным именем SafirMessage; чужой формат отклоняет. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:26>) | `var ns = root.Name.Namespace;` | Берет пространство имен корня. В примере оно пустое, но код поддерживает и одноименные элементы в namespace. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:27>) | `var requisitions = root.Elements(ns + "Requisition").ToArray();` | Выбирает только непосредственные Requisition данного пространства имен. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:28>) | `var results = new List<RequisitionResult>();` | Создает список результатов по заявкам. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:29>) | `var eligible = new HashSet<XElement>();` | Набор тестов, для которых удалось однозначно определить заявку и отделение. Это НЕ набор только измененных тестов. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:30>) | `var count = 0;` | Обнуляет общий счетчик замен. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:31>) | `foreach (var req in requisitions)` | Начинает независимую обработку каждой заявки. |
| [32](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:32>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:33>) | `// Отделение и тесты связываются только внутри текущей заявки.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:34>) | `var id = (string?)req.Attribute("RequisitionID") ?? "не определён";` | Читает точный атрибут RequisitionID; при отсутствии использует «не определён». |
| [35](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:35>) | `var changes = new List<TestChange>();` | Создает список отдельных замен только текущей заявки. |
| [36](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:36>) | `var units = req.Elements(ns + "ReqUnit").ToArray();` | Собирает непосредственные ReqUnit этой заявки; ReceiveUnit и другие подразделения не берутся. |
| [37](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:37>) | `if (units.Length != 1 \|\| units[0].Attribute("AddressCode") == null)` | Если ReqUnit не один либо нет AddressCode, принадлежность считается неоднозначной. |
| [38](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:38>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [39](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:39>) | `results.Add(new(id, "принадлежность к отделению неоднозначна", []));` | Добавляет результат без изменений с понятной причиной. |
| [40](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:40>) | `continue;` | Переходит к следующей заявке, не рассматривая тесты этой. |
| [41](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:41>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [42](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:42>) | `foreach (var analysis in req.Elements(ns + "Reply").Elements(ns + "Sample").Elements(ns + "Analysis"))` | Проходит строго Requisition/Reply/Sample/Analysis, а не произвольные Analysis во всем документе. |
| [43](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:43>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [44](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:44>) | `eligible.Add(analysis);` | Помечает тест как однозначно связанный с отделением, даже если замены для него нет. |
| [45](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:45>) | `if ((string?)units[0].Attribute("AddressCode") != "32" \|\|` | Проверяет первое условие пропуска: отделение не 32. |
| [46](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:46>) | `(string?)analysis.Attribute("TestMethodCode") != "IF_HIV_(2)") continue;` | Либо код не в точности IF_HIV_(2). При выполнении любого условия continue переходит к следующему тесту. Для AddressCode=39 все тесты пропускаются здесь. |
| [47](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:47>) | `var suffix = (string?)analysis.Parent!.Attribute("LaboratoryID") switch` | Берет LaboratoryID у родительского Sample. ! только подавляет предупреждение компилятора о null; структуру родителя обеспечивает выборка строки 42. |
| [48](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:48>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [49](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:49>) | `"1000097520IH" => "_1", "1000097520IG" => "_2", _ => null` | Для IH выбирает _1, для IG - _2, для любого другого значения - null. Порядковый номер теста не участвует. |
| [50](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:50>) | `};` | Завершает выражение/инициализатор и инструкцию. |
| [51](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:51>) | `if (suffix == null) continue;` | Неизвестная лаборатория: пропускает замену. |
| [52](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:52>) | `analysis.SetAttributeValue("TestMethodCode", "IF_HIV_(2)" + suffix);` | Меняет только атрибут TestMethodCode выбранного Analysis. |
| [53](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:53>) | `changes.Add(new((string)analysis.Parent.Attribute("LaboratoryID")!, "IF_HIV_(2)", "IF_HIV_(2)" + suffix, 1));` | Запоминает технические данные этой замены для журнала; другие поля анализа не копирует в лог. |
| [54](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:54>) | `count++;` | Увеличивает суммарный счетчик замен на один. |
| [55](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:55>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [56](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:56>) | `// Одинаковые замены объединяем с количеством; порядок анализов не имеет значения.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [57](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:57>) | `var grouped = changes.GroupBy(c => (c.LaboratoryId, c.OldCode, c.NewCode))` | Группирует одинаковые замены по лаборатории и паре кодов. |
| [58](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:58>) | `.Select(g => new TestChange(g.Key.LaboratoryId, g.Key.OldCode, g.Key.NewCode, g.Count())).ToArray();` | Создает по одной записи на группу; Count содержит число измененных Analysis, а не индекс теста. |
| [59](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:59>) | `var reason = (string?)units[0].Attribute("AddressCode") != "32"` | Готовит причину для ситуации без изменений: проверяет отделение. |
| [60](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:60>) | `? $"AddressCode={(string?)units[0].Attribute("AddressCode")}" : "подходящих тестов нет";` | Для другого отделения записывает AddressCode=...; для 32 - «подходящих тестов нет». При наличии Changes это поле тоже заполнено, но LogResults его не использует. |
| [61](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:61>) | `results.Add(new(id, reason, grouped));` | Добавляет результат заявки, даже если массив изменений пуст. |
| [62](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:62>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [63](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:63>) | `var ambiguous = root.Descendants().Count(e => e.Name.LocalName == "Analysis" && !eligible.Contains(e));` | Считает Analysis, не попавшие в набор однозначно привязанных. Они не изменяются; движок выдаст диагностическое сообщение. |
| [64](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:64>) | `var ids = results.Select(r => r.RequisitionId).ToArray();` | Собирает номера заявок из результатов. |
| [65](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:65>) | `// Без замен возвращаем исходный массив: даже сериализация XML не выполняется.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [66](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:66>) | `if (count == 0) return new(source, 0, ids, ambiguous, results.ToArray());` | Если счетчик нулевой, возвращает ТОТ ЖЕ массив source. Нет сохранения XDocument и нормализации кавычек/пробелов. |
| [67](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:67>) | `var encoding = DetectEncoding(source, document.Declaration?.Encoding);` | Только при реальных заменах определяет исходную кодировку и наличие BOM. |
| [68](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:68>) | `using var output = new MemoryStream();` | Создает выходной поток в памяти. |
| [69](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:69>) | `using (var writer = XmlWriter.Create(output, new XmlWriterSettings` | Создает XmlWriter с параметрами сохранения. |
| [70](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:70>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [71](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:71>) | `Encoding = encoding, Indent = false, OmitXmlDeclaration = document.Declaration == null,` | Сохраняет кодировку, отключает новое форматирование, не добавляет декларацию, если ее не было. |
| [72](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:72>) | `NewLineHandling = NewLineHandling.None, CloseOutput = false` | Не преобразует переводы строк при записи; оставляет MemoryStream открытым после закрытия writer. Парсер уже мог нормализовать XML при чтении. |
| [73](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:73>) | `})) document.Save(writer);` | Сохраняет измененное дерево XML; выход из using завершает/сбрасывает writer. |
| [74](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:74>) | `return new(output.ToArray(), count, ids, ambiguous, results.ToArray());` | Возвращает новые байты и метаданные. Для измененного XML гарантируются значения/структура, а не полное совпадение форматирования. |
| [75](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:75>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [76](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:76>) | — | Пустая строка для читаемости; не выполняется. |
| [77](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:77>) | `private static Encoding DetectEncoding(byte[] b, string? declaration)` | Определяет кодировку по байтам и декларации. |
| [78](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:78>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [79](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:79>) | `// Сначала BOM (UTF-32 до UTF-16 из-за общего начала), затем байтовые признаки` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [80](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:80>) | `// и декларация. Это сохраняет исходную кодировку при сериализации измененного XML.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [81](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:81>) | `if (b.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return new UTF32Encoding(false, true, true);` | BOM FF FE 00 00: UTF-32 little-endian с BOM; проверяется до похожего BOM UTF-16. |
| [82](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:82>) | `if (b.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return new UTF32Encoding(true, true, true);` | BOM 00 00 FE FF: UTF-32 big-endian с BOM. |
| [83](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:83>) | `if (b.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return new UnicodeEncoding(false, true, true);` | BOM FF FE: UTF-16 little-endian с BOM. |
| [84](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:84>) | `if (b.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return new UnicodeEncoding(true, true, true);` | BOM FE FF: UTF-16 big-endian с BOM. |
| [85](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:85>) | `if (b.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return new UTF8Encoding(true, true);` | BOM EF BB BF: UTF-8 с BOM. |
| [86](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:86>) | `if (b.Length >= 4 && b[0] == 0 && b[1] == 0) return new UTF32Encoding(true, false, true);` | Без BOM первые нулевые байты указывают на UTF-32 big-endian. |
| [87](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:87>) | `if (b.Length >= 4 && b[1] == 0 && b[2] == 0) return new UTF32Encoding(false, false, true);` | Альтернативное расположение нулей указывает на UTF-32 little-endian. |
| [88](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:88>) | `if (b.Length >= 2 && b[0] == 0) return new UnicodeEncoding(true, false, true);` | Первый нулевой байт указывает на UTF-16 big-endian без BOM. |
| [89](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:89>) | `if (b.Length >= 2 && b[1] == 0) return new UnicodeEncoding(false, false, true);` | Второй нулевой байт указывает на UTF-16 little-endian без BOM. |
| [90](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:90>) | `var enc = Encoding.GetEncoding(declaration ?? "utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);` | Для остальных случаев читает кодировку из декларации либо использует UTF-8; недопустимые преобразования вызывают исключение вместо тихой замены символов. |
| [91](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:91>) | `return enc.CodePage == 65001 ? new UTF8Encoding(false, true) : enc;` | У UTF-8 без исходного BOM явно оставляет BOM выключенным; для других кодировок возвращает найденный Encoding. |
| [92](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:92>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [93](<C:/Development/BAK lab/src/InternalIntegration/XmlTransformer.cs:93>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |

### 9.6. StateStore.cs — Состояние на диске

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs>). Строк: 74. SHA-256: `e74de6f9b5579e62d94bd80a94756ef923cbcdd79ad8ef27ac4957eae3f2c02c`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:1>) | `using System.Security.Cryptography;` | Подключает SHA256 для контрольных сумм. Это импорт имен, не вызов функции. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:2>) | `using System.Text;` | Подключает Encoding и другие средства текста/кодировок. Это импорт имен, не вызов функции. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:3>) | `using System.Text.Json;` | Подключает сериализацию JSON и JsonException. Это импорт имен, не вызов функции. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:4>) | — | Пустая строка для читаемости; не выполняется. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:5>) | `namespace InternalIntegration;` | Объединяет классы файла в пространство имен InternalIntegration; каталог и процесс не создает. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:6>) | — | Пустая строка для читаемости; не выполняется. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:7>) | `/// <summary>Один JSON на задание: дедлайн PDF и контрольные данные незавершенной публикации.</summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:8>) | `public sealed class PairState` | Класс данных одной пары. Сериализуется в JSON; не выполняет обработку самостоятельно. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:9>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:10>) | `public string Name { get; set; } = "";` | Оригинальное имя XML с расширением, например 007_...xml. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:11>) | `public DateTimeOffset FirstReady { get; set; }` | UTC-момент первого подтверждения готовности XML; основа дедлайна PDF. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:12>) | `public bool PdfSeen { get; set; }` | Был ли PDF уже замечен. true запрещает обходить незавершенный PDF по таймауту. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:13>) | `public string? PdfName { get; set; }` | Фактическое имя PDF с сохранением регистра расширения. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:14>) | `// null: ожидаем файлы; GUID: временные результаты подготовлены.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:15>) | `public string? Transaction { get; set; }` | GUID подготовленной транзакции; null означает, что подготовка еще не зафиксирована. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:16>) | `public string? SourceXmlHash { get; set; }` | SHA-256 исходного XML: используется перед его удалением. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:17>) | `public string? XmlHash { get; set; }` | SHA-256 выходного XML: используется для проверки подготовленного/опубликованного результата. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:18>) | `public string? PdfHash { get; set; }` | SHA-256 PDF. null означает транзакцию без PDF. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:19>) | `public string? XmlIdentity { get; set; }` | Идентификатор NTFS временного XML; сохраняется при его переименовании в итоговый файл. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:20>) | `public string? PdfIdentity { get; set; }` | Аналогичный идентификатор PDF. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:21>) | `public string[] RequisitionIds { get; set; } = [];` | Массив номеров заявок для сообщений о паре; до успешного разбора XML пуст. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:22>) | `public RequisitionResult[] Results { get; set; } = [];` | Подробные решения по заявкам, включая изменения. Значение [] сохраняет совместимость со старыми JSON без этого поля. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:23>) | `public int Replacements { get; set; }` | Общее количество замен во всем XML. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:24>) | `// Намерение пишется ДО переименования, Published - ПОСЛЕ публикации обоих результатов.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:25>) | `public bool PdfIntent { get; set; }` | Намерение опубликовать PDF уже сохранено; не означает само по себе, что файл перемещен. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:26>) | `public bool XmlIntent { get; set; }` | Аналогичное намерение для XML. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:27>) | `public bool Published { get; set; }` | Публикация всех требуемых выходных файлов подтверждена; может оставаться очистка исходников. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:28>) | `public int Failures { get; set; }` | Количество ошибок попыток по этому заданию. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:29>) | `public DateTimeOffset RetryAfter { get; set; }` | Не начинать новую попытку раньше этого момента. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:30>) | `public bool Blocked { get; set; }` | Постоянная блокировка задания: оно пропускается до ручного устранения причины и разблокировки. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:31>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [32](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:32>) | — | Пустая строка для читаемости; не выполняется. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:33>) | `/// <summary>Изолирует чтение/атомарное обновление состояния от обработки XML и мониторинга.</summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:34>) | `public sealed class StateStore(string directory)` | Класс работы с JSON получает путь через primary constructor. |
| [35](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:35>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [36](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:36>) | `public Dictionary<string, PairState> Load()` | Возвращает все сохраненные задания по имени XML. |
| [37](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:37>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [38](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:38>) | `var states = new Dictionary<string, PairState>(StringComparer.OrdinalIgnoreCase);` | Словарь нечувствителен к регистру, как обычные имена Windows. |
| [39](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:39>) | `foreach (var path in Directory.EnumerateFiles(directory, "*.json"))` | Читает только *.json; незаконченные *.json.tmp не загружаются. |
| [40](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:40>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [41](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:41>) | `var state = JsonSerializer.Deserialize<PairState>(File.ReadAllBytes(path)) ?? throw new JsonException();` | Читает байты JSON и восстанавливает PairState. Неверный JSON или значение null вызывает JsonException. |
| [42](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:42>) | `// Совместимый формат сохраняет возможность продолжить задания прежней версии.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [43](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:43>) | `// Не допускаем путей вне рабочих папок через подмененные поля JSON.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [44](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:44>) | `if (!IsName(state.Name) \|\| Path.GetFullPath(path) != Path.GetFullPath(PathFor(state.Name)) \|\|` | Проверяет безопасное имя XML и соответствие имени файла состояния вычисленному пути. |
| [45](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:45>) | `(state.PdfName != null && !IsName(state.PdfName)) \|\|` | Проверяет безопасное имя PDF, если оно задано. |
| [46](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:46>) | `(state.Transaction != null && !Guid.TryParseExact(state.Transaction, "N", out _)) \|\|` | Проверяет GUID транзакции в формате N: 32 шестнадцатеричных символа без дефисов. |
| [47](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:47>) | `state.RequisitionIds == null \|\| state.Results == null \|\| !states.TryAdd(state.Name, state))` | Запрещает null-массивы и дубликаты заданий. TryAdd заодно добавляет исправное задание в словарь; при более ранней ошибке эта часть не вычисляется. |
| [48](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:48>) | `throw new JsonException("Некорректное состояние задания.");` | Сообщает о поврежденном состоянии. Движок остановит текущий обход, а не будет угадывать этап публикации. |
| [49](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:49>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [50](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:50>) | `return states;` | Возвращает заполненный словарь. |
| [51](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:51>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [52](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:52>) | — | Пустая строка для читаемости; не выполняется. |
| [53](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:53>) | `public void Save(PairState state)` | Сохраняет текущее состояние одного задания. |
| [54](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:54>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [55](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:55>) | `var path = PathFor(state.Name);` | Находит имя итогового JSON по имени исходного XML. |
| [56](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:56>) | `// Старый JSON остается пригодным до готовности нового. Временный файл не загружаем.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [57](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:57>) | `WriteDurable(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(state));` | Сериализует состояние в UTF-8 и полностью записывает соседний временный файл. |
| [58](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:58>) | `File.Move(path + ".tmp", path, true);` | Заменяет итоговый JSON уже готовым временным. true разрешает замену собственного состояния, не пользовательского выходного XML. |
| [59](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:59>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [60](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:60>) | — | Пустая строка для читаемости; не выполняется. |
| [61](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:61>) | `public void Delete(PairState state) => File.Delete(PathFor(state.Name));` | Удаляет JSON завершенного задания. |
| [62](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:62>) | — | Пустая строка для читаемости; не выполняется. |
| [63](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:63>) | `public static void WriteDurable(string path, byte[] bytes)` | Общий помощник устойчивой записи байтов: используется и для JSON, и для временного XML. |
| [64](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:64>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [65](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:65>) | `using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);` | Открывает/создает файл с очисткой предыдущего содержимого; другим процессам доступ на время записи запрещен. |
| [66](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:66>) | `stream.Write(bytes);` | Записывает весь переданный массив. |
| [67](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:67>) | `stream.Flush(true);` | Сбрасывает содержимое на диск до фиксации следующего этапа. |
| [68](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:68>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [69](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:69>) | — | Пустая строка для читаемости; не выполняется. |
| [70](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:70>) | `private string PathFor(string name) => Path.Combine(directory,` | Начинает формирование пути файла состояния внутри StateDirectory. |
| [71](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:71>) | `Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()))) + ".json");` | Берет SHA-256 от имени XML в верхнем регистре и добавляет .json. Это хеш ИМЕНИ, а не содержимого XML. |
| [72](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:72>) | `private static bool IsName(string name) => !string.IsNullOrWhiteSpace(name) &&` | Проверяет, что имя непустое. |
| [73](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:73>) | `name == Path.GetFileName(name) && name is not "." and not "..";` | Допускает только имя файла без каталога и без специальных имен . и ... Проверка не является полной схемой валидации всех полей JSON. |
| [74](<C:/Development/BAK lab/src/InternalIntegration/StateStore.cs:74>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |

### 9.7. FileLogger.cs — Текстовый журнал

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs>). Строк: 54. SHA-256: `40df5f98e7625d13bf4eec0f5a2148d126c5a3b37f2bace81a551595445658fa`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:1>) | `using System.Text;` | Подключает Encoding и другие средства текста/кодировок. Это импорт имен, не вызов функции. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:2>) | — | Пустая строка для читаемости; не выполняется. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:3>) | `namespace InternalIntegration;` | Объединяет классы файла в пространство имен InternalIntegration; каталог и процесс не создает. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:4>) | — | Пустая строка для читаемости; не выполняется. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:5>) | `/// <summary>Прямой текстовый журнал: один файл UTF-8 на сутки, без провайдеров и очередей.</summary>` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:6>) | `public sealed class FileLogger` | Самостоятельный класс журналирования. Не реализует ILogger и не подключается как провайдер Host. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:7>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:8>) | `private readonly string directory;` | Сохраняет нормализованный путь папки журнала; readonly запрещает переназначить поле после конструктора. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:9>) | `private readonly TimeProvider clock;` | Источник времени. В обычном запуске системный; в тестах можно подставить управляемые часы. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:10>) | `private readonly object sync = new();` | Общий объект блокировки записей одного экземпляра; разные потоки не смешают строки. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:11>) | — | Пустая строка для читаемости; не выполняется. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:12>) | `public FileLogger(string directory, TimeProvider? clock = null)` | Конструктор принимает папку и необязательные часы. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:13>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:14>) | `if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Не задан каталог журнала.");` | Не позволяет запустить журнал с пустым путем. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:15>) | `this.directory = Path.GetFullPath(directory, AppContext.BaseDirectory);` | Преобразует относительный путь в абсолютный относительно exe. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:16>) | `this.clock = clock ?? TimeProvider.System;` | Использует переданные часы или TimeProvider.System. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:17>) | `Directory.CreateDirectory(this.directory);` | Создает папку журнала, если ее нет. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:18>) | `Info("Журнал открыт");` | Сразу пишет пробное/служебное событие. Так проблема доступа выявляется до обработки документов. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:19>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:20>) | — | Пустая строка для читаемости; не выполняется. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:21>) | `public void Info(string message, string? file = null, string? requisitionId = null) => Write("INFO", message, file, requisitionId);` | Сокращенный метод Info передает уровень INFO в общий Write. Имя файла и номер заявки необязательны для системных событий. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:22>) | `public void Error(string message, string? file = null, string? requisitionId = null) => Write("ERROR", message, file, requisitionId);` | То же для ERROR. Никакой фильтрации категорий и уровней нет. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:23>) | — | Пустая строка для читаемости; не выполняется. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:24>) | `private void Write(string level, string message, string? file, string? requisitionId)` | Единственная реализация формирования и записи строки. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:25>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:26>) | `lock (sync)` | Блокирует параллельную запись другим потоком этого же экземпляра FileLogger. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:27>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:28>) | `// UTC указан явно: часовой пояс Windows не влияет на порядок записей.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:29>) | `var now = clock.GetUtcNow();` | Получает UTC-время, независимо от текущего часового пояса Windows. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:30>) | `var line = $"{now:O} \| {level} \| file={Clean(file ?? "-")} \| " +` | Формирует первую часть строки: формат O дает дату/время со смещением; далее уровень и имя файла. Для отсутствующего имени используется дефис. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:31>) | `$"requisitionID={Clean(requisitionId ?? "не определён")} \| {Clean(message)}{Environment.NewLine}";` | Добавляет номер заявки, сообщение и платформенный перевод строки. Отсутствующий номер заменяет на «не определён». |
| [32](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:32>) | `try` | Начинает защищенный блок: подходящие исключения из него обрабатываются в последующих catch. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:33>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:34>) | `var path = Path.Combine(directory, $"InternalIntegration-{now:yyyyMMdd}.log");` | Выбирает файл по дате UTC; после полуночи записи автоматически идут в новый файл. |
| [35](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:35>) | `using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);` | Открывает файл на дописывание, а не перезапись. FileShare.Read позволяет читать журнал другим процессам; using закроет файл в конце блока. |
| [36](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:36>) | `stream.Write(Encoding.UTF8.GetBytes(line));` | Преобразует строку в UTF-8 и пишет байты в открытый поток. |
| [37](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:37>) | `stream.Flush(true);` | Сбрасывает буфер потока на диск. Это не абсолютная гарантия от аппаратной потери питания. |
| [38](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:38>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [39](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:39>) | `catch (Exception error) when (error is IOException or UnauthorizedAccessException)` | Локально перехватывает ошибки записи/прав доступа. |
| [40](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:40>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [41](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:41>) | `// При недоступности единственного журнала останавливаем обработку.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [42](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:42>) | `// Исходники/состояние сохраняются для последующего восстановления.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [43](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:43>) | `throw new FileLogException(error);` | Заворачивает ошибку в FileLogException. PairEngine не считает ее обычной временной ошибкой пары: она поднимается к Worker. |
| [44](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:44>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [45](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:45>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [46](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:46>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [47](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:47>) | — | Пустая строка для читаемости; не выполняется. |
| [48](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:48>) | `// Не даем значениям из XML/имен файлов вставить поддельную строку журнала.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [49](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:49>) | `// Содержимое документов и Exception.Message вызывающий код сюда не передает.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [50](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:50>) | `private static string Clean(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n")` | Clean заменяет CR и LF на видимые последовательности, чтобы данные не создали новую строку журнала. |
| [51](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:51>) | `.Replace("\t", "\\t").Replace("\|", "\\u007C").Replace("\u2028", " ").Replace("\u2029", " ");` | Экранирует табуляцию и разделитель &#124;; убирает два Unicode-разделителя строк. Не является универсальным обезличиванием: вызывающий код не должен передавать персональные данные. |
| [52](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:52>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [53](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:53>) | — | Пустая строка для читаемости; не выполняется. |
| [54](<C:/Development/BAK lab/src/InternalIntegration/FileLogger.cs:54>) | `public sealed class FileLogException(Exception inner) : Exception("Не удалось записать текстовый журнал.", inner);` | Отдельный тип ошибки записи. InnerException сохраняется в памяти для диагностики, но его текст приложение в журнал не выводит. |

### 9.8. FileIdentity.cs — Идентификатор Windows

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs>). Строк: 39. SHA-256: `f49af441326d8aa04a01d0637fac4f4ba8cb82546f4b677b4ac6f8f6e3642032`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:1>) | `using System.ComponentModel;` | Подключает Win32Exception для кодов ошибок Windows. Это импорт имен, не вызов функции. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:2>) | `using System.Runtime.InteropServices;` | Подключает P/Invoke и маршалинг структур Windows. Это импорт имен, не вызов функции. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:3>) | `using Microsoft.Win32.SafeHandles;` | Подключает безопасный файловый дескриптор для WinAPI. Это импорт имен, не вызов функции. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:4>) | — | Пустая строка для читаемости; не выполняется. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:5>) | `namespace InternalIntegration;` | Объединяет классы файла в пространство имен InternalIntegration; каталог и процесс не создает. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:6>) | — | Пустая строка для читаемости; не выполняется. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:7>) | `// При переименовании идентификатор NTFS сохраняется. Чужой файл даже с теми же` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:8>) | `// байтами должен считаться конфликтом, а не нашей прерванной публикацией.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:9>) | `internal static class FileIdentity` | Внутренний статический помощник Windows. Объекты этого класса создавать не нужно. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:10>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:11>) | `public static string Read(string path)` | Возвращает строковый идентификатор файлового объекта по пути. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:12>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:13>) | `if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Требуется Windows и файловая система с устойчивыми идентификаторами файлов.");` | Отклоняет запуск этой операции вне Windows. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:14>) | `using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);` | Открывает файл для чтения и получает безопасный дескриптор SafeFileHandle. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:15>) | `if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))` | Вызывает WinAPI; out info получает структуру результата. false означает ошибку. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:16>) | `throw new IOException("Не удалось получить идентификатор файла", new Win32Exception(Marshal.GetLastWin32Error()));` | Преобразует ошибку WinAPI в IOException с сохраненным системным кодом. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:17>) | `return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";` | Склеивает серийный номер тома и 64-битный индекс файла из двух половин. Формат X8: восемь шестнадцатеричных цифр с ведущими нулями. |
| [18](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:18>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [19](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:19>) | — | Пустая строка для читаемости; не выполняется. |
| [20](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:20>) | `// Единственный платформенный вызов: получаем номер тома и индекс файла Windows.` | Комментарий для читателя/IDE; не исполняется. Поясняет соседний код. |
| [21](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:21>) | `[DllImport("kernel32.dll", SetLastError = true)]` | P/Invoke: функция находится в kernel32.dll; SetLastError сохраняет системный код ошибки. |
| [22](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:22>) | `[return: MarshalAs(UnmanagedType.Bool)]` | Указывает, как преобразовать возвращаемый Windows BOOL в C# bool. |
| [23](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:23>) | `private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);` | Объявляет внешнюю функцию. Ее реализация находится в Windows, а не в исходниках проекта. |
| [24](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:24>) | — | Пустая строка для читаемости; не выполняется. |
| [25](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:25>) | `[StructLayout(LayoutKind.Sequential)]` | Поля структуры должны идти в памяти точно в объявленном порядке для совместимости с WinAPI. |
| [26](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:26>) | `private struct HandleInformation` | Структура соответствует BY_HANDLE_FILE_INFORMATION. |
| [27](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:27>) | `{` | Открывает блок или инициализатор предшествующей конструкции. |
| [28](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:28>) | `public uint FileAttributes;` | Атрибуты файла; здесь напрямую не используются, но поле нужно для правильного смещения следующих полей. |
| [29](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:29>) | `public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;` | Время создания в формате Windows FILETIME. |
| [30](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:30>) | `public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;` | Время последнего доступа; также часть обязательной структуры. |
| [31](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:31>) | `public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;` | Время последней записи. |
| [32](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:32>) | `public uint VolumeSerialNumber;` | Серийный номер тома, включается в результат Read. |
| [33](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:33>) | `public uint FileSizeHigh;` | Старшие 32 бита размера файла. |
| [34](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:34>) | `public uint FileSizeLow;` | Младшие 32 бита размера файла. |
| [35](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:35>) | `public uint NumberOfLinks;` | Количество жестких ссылок на файловый объект. |
| [36](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:36>) | `public uint FileIndexHigh;` | Старшие 32 бита индекса файла. |
| [37](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:37>) | `public uint FileIndexLow;` | Младшие 32 бита индекса файла. Вместе с томом отличает чужой файл с теми же байтами. |
| [38](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:38>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |
| [39](<C:/Development/BAK lab/src/InternalIntegration/FileIdentity.cs:39>) | `}` | Закрывает ближайший блок. using-переменные этого блока освобождаются при выходе. |

### 9.9. appsettings.json — Конфигурация

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json>). Строк: 15. SHA-256: `d1ba2f38b9357d18ddf437d3d8bcd2e983426e41c431e358a1f3a3a4251fed9a`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:1>) | `{` | Начало объекта JSON. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:2>) | `"FileProcessing": {` | Раздел, привязываемый к ProcessingOptions. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:3>) | `"InputDirectory": "C:\\XmlProcessor\\Input",` | Полный путь входа; двойной обратный слеш в JSON кодирует один слеш Windows. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:4>) | `"OutputDirectory": "C:\\XmlProcessor\\Output",` | Полный путь итоговых XML/PDF. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:5>) | `"StateDirectory": "C:\\XmlProcessor\\State",` | Каталог устойчивого состояния заданий. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:6>) | `"ScanIntervalSeconds": 10,` | Пауза между обходами 10 секунд. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:7>) | `"FileStabilitySeconds": 3,` | Требуемая стабильность файла минимум 3 секунды. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:8>) | `"PdfWaitTimeoutSeconds": 300,` | Отсутствующий PDF ожидается 300 секунд после FirstReady. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:9>) | `"RetryDelaySeconds": 5,` | Новая попытка не раньше чем через 5 секунд после ошибки. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:10>) | `"MaxRetryAttempts": 5` | Пять временных ошибок приводят к блокировке. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:11>) | `},` | Конец FileProcessing; запятая разделяет соседние разделы. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:12>) | `"FileLogging": {` | Отдельный раздел для прямого файлового журнала. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:13>) | `"DirectoryPath": "C:\\XmlProcessor\\Logs"` | Каталог дневных .log-файлов. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:14>) | `}` | Конец FileLogging. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/appsettings.json:15>) | `}` | Конец корневого объекта JSON. |

### 9.10. InternalIntegration.csproj — Параметры сборки

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj>). Строк: 16. SHA-256: `b45cabd44494ecc24e8900a07bf96258afd2be5c3676eacd81963ea074ccc427`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:1>) | `<Project Sdk="Microsoft.NET.Sdk.Worker">` | Выбирает Worker SDK MSBuild. Это описание сборки, а не выполняемый C#. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:2>) | `<PropertyGroup>` | Начало свойств сборки. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:3>) | `<TargetFramework>net9.0</TargetFramework>` | Целевая платформа .NET 9. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:4>) | `<ImplicitUsings>enable</ImplicitUsings>` | Автоматически добавляет типовые using: System, коллекции, IO и др.; поэтому они не перечислены в каждом .cs. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:5>) | `<Nullable>enable</Nullable>` | Включает анализ nullable-ссылок. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:6>) | `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` | Предупреждения компиляции основного проекта считаются ошибками. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:7>) | `<Product>Internal Integration</Product>` | Имя продукта в метаданных сборки. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:8>) | `<Title>Internal Integration</Title>` | Название сборки для отображения. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:9>) | `<Description>Обработка пар XML/PDF Safir</Description>` | Описание продукта. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:10>) | `</PropertyGroup>` | Конец свойств. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:11>) | `<ItemGroup>` | Начало списка зависимостей. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:12>) | `<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.9" />` | Пакет стандартного .NET Host указанной версии. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:13>) | `<PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="9.0.9" />` | Пакет поддержки Windows Service указанной версии. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:14>) | `</ItemGroup>` | Конец списка зависимостей. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:15>) | `<ItemGroup><None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>` | Копирует appsettings.json к результату сборки, если файл новее имеющейся копии. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/InternalIntegration.csproj:16>) | `</Project>` | Конец проекта. |

### 9.11. Properties/PublishProfiles/WindowsFolder.pubxml — Профиль публикации

[Открыть исходник](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml>). Строк: 17. SHA-256: `56a43d74c184a2746d898c3c996bf40984c70621e3b65aeeb4633db4d9a264f1`.

| Строка | Код | Что выполняется |
| --- | --- | --- |
| [1](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:1>) | `<?xml version="1.0" encoding="utf-8"?>` | XML-декларация профиля публикации, не декларация обрабатываемого Safir XML. |
| [2](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:2>) | `<Project>` | Корень MSBuild-профиля. |
| [3](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:3>) | `<PropertyGroup>` | Начало свойств публикации. |
| [4](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:4>) | `<Configuration>Release</Configuration>` | Публикует Release-конфигурацию. |
| [5](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:5>) | `<Platform>Any CPU</Platform>` | Платформа сборки Any CPU; конкретная ОС/архитектура определяется RuntimeIdentifier. |
| [6](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:6>) | `<PublishProtocol>FileSystem</PublishProtocol>` | Публикация в файловую систему. |
| [7](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:7>) | `<_TargetId>Folder</_TargetId>` | Цель Folder для интерфейса Visual Studio. |
| [8](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:8>) | `<TargetFramework>net9.0</TargetFramework>` | Платформа net9.0. |
| [9](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:9>) | `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` | Итоговая среда Windows x64. |
| [10](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:10>) | `<SelfContained>true</SelfContained>` | Автономный комплект включает .NET Runtime. |
| [11](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:11>) | `<PublishSingleFile>false</PublishSingleFile>` | Не объединяет все в один exe: переносить нужно целую папку. |
| [12](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:12>) | `<PublishTrimmed>false</PublishTrimmed>` | Не удаляет предполагаемо неиспользуемый код библиотек. |
| [13](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:13>) | `<PublishReadyToRun>false</PublishReadyToRun>` | Не включает предварительную ReadyToRun-компиляцию. |
| [14](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:14>) | `<PublishDir>$(MSBuildProjectDirectory)\..\..\publish\win-x64\</PublishDir>` | Путь результата MSBuild от каталога проекта: на два уровня вверх, затем publish/win-x64. |
| [15](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:15>) | `<PublishUrl>$(MSBuildProjectDirectory)\..\..\publish\win-x64\</PublishUrl>` | Путь результата для Visual Studio; совпадает с PublishDir. |
| [16](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:16>) | `</PropertyGroup>` | Конец группы свойств. |
| [17](<C:/Development/BAK lab/src/InternalIntegration/Properties/PublishProfiles/WindowsFolder.pubxml:17>) | `</Project>` | Конец профиля. |

## 10. Как пройти этот код в Visual Studio

1. Откройте Internal Integration.sln, выберите основной проект запускаемым, конфигурацию Debug.
2. Для самостоятельной отладки задайте отдельные учебные Input/Output/State/Logs. Никогда не помещайте единственный оригинал в рабочий вход: при успехе служба удаляет входные файлы.
3. Поставьте breakpoint в Program на `options.Validate()` и нажмите F5. F10 выполняет строку без захода, F11 заходит в вызванный метод, Shift+F11 выходит к вызывающему, F5 продолжает до следующей остановки.
4. Поставьте точки на Worker.ExecuteAsync, PairEngine.Scan, Ready, PrepareFiles и XmlTransformer.Transform. Для небольшого примера дальнейшие важные точки: XDocument.Load, проверка AddressCode, ранний return при count==0, PublishOne, LogResults и DeleteVerified.
5. В Ready смотрите `old`, `info.Length`, `info.LastWriteTimeUtc`, `now`, `old.Since`. На первом вызове old может быть не найден: это нормальная регистрация наблюдения.
6. В Process смотрите `state.FirstReady`, `PdfSeen`, `Transaction`, `Published`. Пока Transaction=null, подготовка не завершена.
7. Для оригинального XML ожидайте XmlException при загрузке. Чтобы увидеть место бросания, откройте «Отладка → Окна → Параметры исключений» и включите остановку при возникновении System.Xml.XmlException. После продолжения Scan обработает это исключение и сохранит Blocked.
8. Учебные варианты без DOCTYPE и с отделением 32 нужны только чтобы исследовать следующие ветви. В данном разборе они были созданы на временных копиях; рабочая защита от DTD не менялась.
9. В трансформаторе смотрите `id`, `units`, `count`, `suffix`, `changes`. Не выводите в журнал/скриншоты полное дерево документа с пациентскими полями.
10. Остановите консоль через Ctrl+C для штатного завершения. Не нажимайте F5 повторно на той же паре, пока первый процесс удерживает .processor.lock.

Разбор помогает читать существующую реализацию, но не меняет ее поведение. В частности, документировать блокировку XML с DOCTYPE не означает автоматически разрешить такой вход.
