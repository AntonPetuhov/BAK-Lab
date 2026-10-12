from pathlib import Path
from xml.sax.saxutils import escape
from reportlab.pdfgen import canvas
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, PageBreak
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.colors import HexColor, white
from reportlab.lib.enums import TA_LEFT
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'output/pdf/Internal Integration - Руководство.pdf'
OUT.parent.mkdir(parents=True, exist_ok=True)
pdfmetrics.registerFont(TTFont('Arial', 'C:/Windows/Fonts/arial.ttf'))
pdfmetrics.registerFont(TTFont('Arial-Bold', 'C:/Windows/Fonts/arialbd.ttf'))
pdfmetrics.registerFontFamily('Arial', normal='Arial', bold='Arial-Bold')
styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name='BodyRU', fontName='Arial', fontSize=10.2, leading=15, spaceAfter=9, textColor=HexColor('#233449')))
styles.add(ParagraphStyle(name='TitleRU', fontName='Arial-Bold', fontSize=29, leading=34, spaceAfter=16, textColor=HexColor('#132A46')))
styles.add(ParagraphStyle(name='HeadRU', fontName='Arial-Bold', fontSize=20, leading=25, spaceAfter=16, textColor=HexColor('#132A46')))
styles.add(ParagraphStyle(name='SubRU', fontName='Arial-Bold', fontSize=12, leading=17, spaceBefore=10, spaceAfter=8, textColor=HexColor('#087F8C')))
styles.add(ParagraphStyle(name='SmallRU', parent=styles['BodyRU'], fontSize=8.4, leading=12))
styles.add(ParagraphStyle(name='CodeRU', fontName='Arial', fontSize=9, leading=13, spaceAfter=12, backColor=HexColor('#EDF3F8'), borderPadding=10))
story = []
def p(text, style='BodyRU'): story.append(Paragraph(text, styles[style]))
def heading(text): p(text, 'HeadRU')
def sub(text): p(text, 'SubRU')
def step(n, text): p(f'<b>{n:02d}.</b> {text}')
def page(): story.append(PageBreak())
def table(rows, widths):
    data = [[Paragraph(escape(str(v)), styles['SmallRU']) for v in row] for row in rows]
    t = Table(data, colWidths=widths, hAlign='LEFT')
    t.setStyle(TableStyle([('BACKGROUND',(0,0),(-1,0),HexColor('#DDEBF1')),('VALIGN',(0,0),(-1,-1),'TOP'),('LEFTPADDING',(0,0),(-1,-1),9),('RIGHTPADDING',(0,0),(-1,-1),9),('TOPPADDING',(0,0),(-1,-1),8),('BOTTOMPADDING',(0,0),(-1,-1),8),('LINEBELOW',(0,0),(-1,-1),.5,HexColor('#D3DEE6'))]))
    story.append(t); story.append(Spacer(1,12))

p('INTERNAL INTEGRATION / РУКОВОДСТВО', 'SubRU')
p('Internal Integration', 'TitleRU')
p('Обработка XML и PDF.<br/>Сборка через Microsoft Visual Studio.', 'HeadRU')
p('Упрощенная версия, 12 октября 2026 года. Windows x64, .NET 9. Для сборки, тестирования и публикации по этой инструкции PowerShell и командная строка не нужны.')
sub('Порядок обработки')
step(1, 'Дождаться стабильности размера и времени изменения XML и освобождения файла от записи.')
step(2, 'Сразу разобрать XML и подготовить замены в памяти. Исходник не редактируется.')
step(3, 'Искать одноименный PDF в течение <b>PdfWaitTimeoutSeconds</b>: по умолчанию <b>10 секунд</b>.')
step(4, 'Если PDF обнаружен, дождаться завершения записи, даже после таймаута. Опубликовать PDF, затем XML. Если PDF не обнаружен, опубликовать XML отдельно.')
step(5, 'После успешной публикации удалить соответствующие исходники. Записать в лог результат с PDF или без него.')
sub('Что упрощено')
p('Один последовательный цикл сканирования, по умолчанию раз в секунду. Нет FileSystemWatcher, параллельной обработки, JSON-состояния и автоматического восстановления. Ожидание одной заявки не блокирует другие.')
p('<b>После перезапуска таймер начинается заново. Прерванная публикация требует ручного разбора. Существующие выходные файлы не перезаписываются.</b>')

page(); heading('1. Правила и фактические поля XML')
p('Корень: SafirMessage. Для каждой Requisition отделение определяется отдельно. Должен быть ровно один непосредственный ReqUnit с атрибутом AddressCode.')
table([['Назначение', 'Путь относительно Requisition'], ['Номер заявки в журнале', '@RequisitionID'], ['Отделение', 'ReqUnit/@AddressCode'], ['Лаборатория образца', 'Reply/Sample/@LaboratoryID'], ['Код теста', 'Reply/Sample/Analysis/@TestMethodCode']], [190,309])
p('При <b>AddressCode="32"</b> заменяется только точный код <b>IF_HIV_(2)</b>:')
table([['LaboratoryID', 'Новый код'], ['1000097520IH', 'IF_HIV_(2)_1'], ['1000097520IG', 'IF_HIV_(2)_2']], [240,259])
p('Заменяются все совпадения независимо от порядка. Другие коды и LaboratoryID, уже замененные коды остаются прежними. При неоднозначной принадлежности теста замена пропускается с диагностикой.')
sub('DOCTYPE и сохранение данных')
p('Объявление DOCTYPE игнорируется. DTD не загружается из файла или сети, внешние сущности не раскрываются. Ссылка на сущность, заданную только в DTD, вызовет ошибку XML.')
p('Без замен XML сохраняется байт в байт, включая DOCTYPE. При заменах сохраняются кодировка, наличие XML-декларации, пространства имен и остальные значения. DOCTYPE при сериализации измененного XML не записывается; форматирование может нормализоваться. PDF всегда копируется без изменений.')
sub('Предоставленный пример')
p('Заявка <b>1000097520</b> имеет <b>ReqUnit AddressCode="39"</b>. Несмотря на подходящие коды и лаборатории, замены не нужны. XML и PDF должны сохраниться байт в байт. Для проверки используйте копии, а не исходные примеры проекта.')

page(); heading('2. Подготовить другой компьютер')
step(1, 'Установите <b>Visual Studio 2022 17.14</b> и <b>.NET 9 SDK</b>. В Visual Studio Installer включите поддержку разработки приложений .NET. Если решение сообщает об отсутствии SDK, добавьте .NET 9 SDK через установщик.')
step(2, 'Скопируйте папку проекта с <b>Internal Integration.sln</b>, <b>src</b> и <b>tests</b>. Откройте файл решения двойным щелчком или через Файл → Открыть → Проект или решение.')
step(3, 'В Обозревателе решений щелкните решение правой кнопкой → <b>Восстановить пакеты NuGet / Restore NuGet Packages</b>. Нужен доступ к источнику пакетов и интернету для отсутствующих пакетов.')
step(4, 'Откройте <b>src / InternalIntegration / appsettings.json</b> и настройте папки. В JSON обратный слеш удваивается: C:\\\\XmlProcessor\\\\Input. Для тестового запуска используйте отдельные пустые папки.')
table([['Параметр', 'Значение по умолчанию'], ['InputDirectory / OutputDirectory', 'C:/XmlProcessor/Input и Output'], ['ScanIntervalSeconds', '1 - период обхода папки'], ['FileStabilitySeconds', '3 - минимальная стабильность файла'], ['PdfWaitTimeoutSeconds', '10 - ожидание отсутствующего PDF'], ['RetryDelaySeconds / MaxRetryAttempts', '5 секунд / 5 попыток до публикации'], ['FileLogging:DirectoryPath', 'C:/XmlProcessor/Logs']], [270,229])
p('Input и Output должны быть разными и невложенными. Относительные пути считаются от папки exe. Изменения применяются после перезапуска. Время ожидания PDF округляется до очередного обхода; таймаут 0 разрешен.', 'SmallRU')

page(); heading('3. Собрать и проверить в Visual Studio')
step(1, 'Выберите в верхней панели <b>Release / Any CPU</b>. Откройте <b>Сборка → Собрать решение / Build → Build Solution</b> или нажмите Ctrl+Shift+B. В окне Вывод не должно быть ошибок.')
step(2, 'Откройте <b>Тест → Обозреватель тестов / Test → Test Explorer</b> и выберите <b>Запустить все / Run All</b>. В этой версии: <b>45 тестов</b>. Они используют синтетические данные и не изменяют примеры в корне.')
step(3, 'Щелкните проект <b>Internal Integration</b> правой кнопкой → <b>Назначить запускаемым проектом / Set as Startup Project</b>. Для отладки можно выбрать Debug.')
step(4, 'Нажмите <b>F5</b> для запуска с отладчиком или <b>Ctrl+F5</b> для запуска без отладчика. Откроется консоль; сообщения нужно смотреть в текстовом журнале.')
step(5, 'Положите копии XML/PDF с одинаковым именем в тестовую входную папку. После успешной обработки они появятся в выходной папке, а входные копии будут удалены.')
step(6, 'Проверьте отдельно XML без PDF: после стабилизации XML и 10 секунд ожидания появится выходной XML. Затем проверьте пару: PDF должен опубликоваться первым.')
step(7, 'Нажмите <b>Ctrl+C в консоли</b> для штатной остановки. Остановка отладчиком может прервать публикацию; автоматического восстановления нет.')
sub('Результат обычной сборки')
p('src / InternalIntegration / bin / Release / net9.0 / InternalIntegration.exe. Рядом нужны конфигурация и библиотеки. Для переноса на компьютер без установленного Runtime используйте автономную публикацию со следующей страницы.')
sub('Если сборка не удалась')
p('Проверьте установленный SDK, окно Вывод и восстановление NuGet. Источники NuGet доступны через Сервис → Параметры → Диспетчер пакетов NuGet → Источники пакетов. Ошибки прав папок смотрите в журнале и настройках учетной записи.')

page(); heading('4. Опубликовать и перенести')
step(1, 'Щелкните проект правой кнопкой → <b>Опубликовать / Publish</b>. Выберите готовый профиль <b>WindowsFolder</b>. Если нужен мастер, выберите <b>Папка / Folder</b>.')
step(2, 'В параметрах профиля проверьте значения:')
table([['Параметр', 'Значение'], ['Configuration', 'Release'], ['Target framework', 'net9.0'], ['Deployment mode', 'Self-contained / Автономное'], ['Target runtime', 'win-x64'], ['Produce single file / Trim unused code', 'Оба выключены'], ['Target location', 'publish / win-x64 в корне решения']], [280,219])
step(3, 'Нажмите <b>Опубликовать / Publish</b>. При первой публикации могут скачиваться пакеты среды выполнения. Дождитесь успешного завершения.')
step(4, 'Перенесите через Проводник <b>всю папку</b> результата, включая InternalIntegration.exe, appsettings.json и библиотеки. Это комплект файлов, а не один автономный exe.')
step(5, 'Настройте пути на целевом компьютере. Для пробного консольного запуска откройте exe двойным щелчком. Отдельная установка .NET Runtime для автономного комплекта не требуется.')
sub('Регистрация Windows Service')
p('Сборка и Publish не регистрируют службу. Проект не содержит графического установщика. Административные команды установки приведены в README; сборка и получение комплекта полностью выполняются через Visual Studio.')
p('Техническое имя службы: <b>InternalIntegration</b>. После регистрации запуск, остановка и настройка учетной записи доступны в оснастке «Службы». Учетной записи нужны права чтения/выполнения приложения, чтения/создания/удаления файлов Input и Output, записи Logs и входа в систему как служба. Запуск под SCM в этой доработке не проверялся.', 'SmallRU')

page(); heading('5. Журнал и ручной разбор ошибок')
sub('Текстовый журнал')
p('Собственный FileLogger дописывает UTF-8-файл <b>InternalIntegration-yyyyMMdd.log</b> в FileLogging:DirectoryPath. Время UTC. Файл можно открыть Блокнотом. ILogger и Event Log для прикладного журнала не используются.')
p('Каждое событие обработки содержит имя XML и requisitionID. Сначала пишутся подготовленные замены: LaboratoryID, старый код, новый код и количество; либо «Без изменений» с причиной. Затем отдельной строкой - итог публикации с PDF или без него. Подготовленная замена еще не означает успешную публикацию.')
p('Содержимое XML/PDF и данные пациентов не записываются. Если XML поврежден и номер не прочитан, указывается «не определён». Недоступность журнала останавливает обработчик. Архивирование логов выполняет администратор.')
sub('Сбой и конфликт имен')
p('Поврежденный XML и конфликт блокируют соответствующий файл до перезапуска, сохраняя исходники. Временные ошибки до публикации повторяются с настроенным интервалом и пределом попыток. Остальные заявки продолжают обрабатываться.')
p('<b>Пара не атомарна:</b> сбой между PDF и XML оставит частичный результат. Остановите службу, сохраните копии входа и выхода, проверьте журнал. Для повторной обработки перенесите конфликтующие результаты в архив и восстановите полную исходную пару во входе. Только затем запускайте службу. Полный выходной комплект не отправляйте повторно без проверки у потребителя.')
p('После успешной публикации сбой между удалениями исходников может оставить только один исходник. Восстановите пару из проверенного выхода/резервной копии, если требуется повторная обработка. Временные .tmp после аварии автоматически не публикуются; разбирайте их при остановленной службе.', 'SmallRU')
sub('При переходе со старой версии')
p('Сначала остановите старую службу и разберите задания в прежних State и .staging. Новая версия эти папки не читает. Поздний PDF после отдельной публикации XML остается на входе для ручного разбора. Отправитель не должен изменять или заменять уже переданные файлы.', 'SmallRU')
def decorate(c, doc):
    c.setTitle('Internal Integration - описание и сборка в Visual Studio')
    c.setAuthor('Internal Integration')
    c.setStrokeColor(HexColor('#D3DEE6')); c.line(48, 51, 547, 51)
    c.setFont('Arial', 8); c.setFillColor(HexColor('#63758B'))
    c.drawString(48, 36, 'INTERNAL INTEGRATION   /   VISUAL STUDIO')
    c.drawRightString(547, 36, str(doc.page))

doc = SimpleDocTemplate(str(OUT), pagesize=(595.28,841.89), rightMargin=48, leftMargin=48, topMargin=42, bottomMargin=65)
doc.build(story, onFirstPage=decorate, onLaterPages=decorate)
print(OUT)

