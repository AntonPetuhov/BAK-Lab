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
p('Обработка пар XML/PDF Safir.<br/>Сборка на другом компьютере через Microsoft Visual Studio.', 'HeadRU')
p('Версия документа: 30 сентября 2026 года. Платформа: Windows x64, .NET 9. Все действия сборки, тестирования и публикации ниже выполняются через окна и меню Visual Studio. Командная строка и PowerShell не нужны.')
sub('Назначение')
p('Программа следит за входной папкой, находит одноименные XML и PDF, при необходимости меняет коды анализов и публикует результат. Один исполняемый файл работает в консольном режиме или как Windows Service. Штатно одна пара файлов относится к одной заявке.')
sub('Точные правила замены')
p('У заявки должен быть единственный непосредственный элемент <b>ReqUnit</b> с <b>AddressCode="32"</b>. Меняется только точный исходный код <b>IF_HIV_(2)</b> в атрибуте <b>Analysis/@TestMethodCode</b>. Идентификатор лаборатории берется из родительского <b>Sample/@LaboratoryID</b>.')
table([['LaboratoryID', 'Новый TestMethodCode'], ['1000097520IH','IF_HIV_(2)_1'],['1000097520IG','IF_HIV_(2)_2']], [235,264])
p('Полный путь: SafirMessage / Requisition / Reply / Sample / Analysis. Отделение: Requisition / ReqUnit. Номер заявки: Requisition / @RequisitionID. Если заявок несколько, каждая обрабатывается отдельно в своем контексте.')
p('Другие коды, лаборатории и уже замененные значения сохраняются. При нуле замен XML остается байт в байт. PDF всегда переносится без изменения. Приложенный пример имеет AddressCode="39" и не должен менять XML.', 'SmallRU')

page(); heading('1. Подготовить другой компьютер')
step(1, 'Установите <b>Visual Studio 2022 версии 17.14 с актуальными обновлениями</b>. Это подходящая версия для используемого .NET 9 SDK. Visual Studio Code не заменяет Visual Studio в этой инструкции.')
step(2, 'В <b>Visual Studio Installer</b> нажмите <b>Изменить / Modify</b>. Выберите нагрузку <b>Разработка классических приложений .NET / .NET desktop development</b>. В разделе отдельных компонентов проверьте наличие <b>.NET 9 SDK</b>; если компонента нет, установите SDK через официальный графический установщик Microsoft [1, 2]. Нужен SDK, а не только Runtime.')
step(3, 'Скопируйте папку проекта на другой компьютер обычным Проводником. Для сборки требуются решение и обе папки исходников из таблицы ниже. Примеры XML/PDF не обязательны. Папки bin, obj, .vs и старый publish можно не переносить.')
table([['Что перенести', 'Назначение'], ['Internal Integration.sln', 'Решение Visual Studio'], ['src / InternalIntegration /', 'Программа, appsettings.json и профиль публикации'], ['tests / InternalIntegration.Tests /', 'Автоматические проверки'], ['README.md и output / pdf /', 'Описание и это руководство']], [250,249])
step(4, 'Откройте Visual Studio: <b>Открыть проект или решение / Open a project or solution</b>. Выберите <b>Internal Integration.sln</b>. В обозревателе решения должны появиться Internal Integration и Internal Integration.Tests.')
step(5, 'Для первой сборки нужен интернет к NuGet. Щелкните решение правой кнопкой и выберите <b>Восстановить пакеты NuGet / Restore NuGet Packages</b>, если пакеты не восстановились автоматически. Дождитесь завершения в окне <b>Вывод / Output</b>.')
p('Проект использует Microsoft.Extensions.Hosting, WindowsServices и пакеты тестирования xUnit. Их не нужно добавлять вручную: версии уже записаны в файлах проектов.', 'SmallRU')

page(); heading('2. Настроить папки и ожидание')
step(1, 'В обозревателе решения откройте <b>src / InternalIntegration / appsettings.json</b>. Измените пути InputDirectory, OutputDirectory и StateDirectory на доступные локальные папки NTFS. Например:')
p('"InputDirectory": "C:\\InternalIntegration\\Input",<br/>"OutputDirectory": "C:\\InternalIntegration\\Output",<br/>"StateDirectory": "C:\\InternalIntegration\\State"'.replace('\\', '\\\\'), 'CodeRU')
p('В JSON каждый обратный слеш внутри строки записывается дважды. Сохраняйте кавычки и запятые. Не заменяйте целиком файл тремя строками выше: это только фрагмент раздела FileProcessing.')
step(2, 'Убедитесь, что три папки различаются и не вложены друг в друга. Программа создаст их, если у текущей учетной записи есть права. Относительные пути считаются от папки приложения; для первого запуска удобнее абсолютные пути.')
table([['Параметр', 'По умолчанию', 'Что означает'],['ScanIntervalSeconds','10','Повторное сканирование'],['FileStabilitySeconds','3','Проверка стабильности файла'],['PdfWaitTimeoutSeconds','300','Ожидание PDF после готовности XML'],['RetryDelaySeconds','5','Пауза перед повторной попыткой'],['MaxRetryAttempts','5','Лимит ошибок обработки']], [215,85,199])
step(3, 'Сохраните файл через <b>Файл → Сохранить все / File → Save All</b>. Изменения параметров обработки применяются после перезапуска программы. При сборке файл копируется к исполняемому файлу.')
sub('Как работает ожидание')
p('PDF может прийти раньше или позже XML. Дедлайн сохраняется в State и не сбрасывается событиями или перезапуском. Если PDF не появился за 300 секунд, XML публикуется отдельно с записью номера заявки в журнал. Если PDF уже замечен, программа дождется окончания его записи, даже после дедлайна.')
p('PDF, поступивший после завершенной публикации XML без PDF, остается во входной папке для ручного разбора. Не удаляйте State и не используйте рабочую входную папку для пробного запуска.', 'SmallRU')

page(); heading('3. Собрать, проверить и запустить')
step(1, 'В верхней панели Visual Studio выберите <b>Release</b> и <b>Any CPU</b>. Если панель скрыта, откройте <b>Сборка → Диспетчер конфигураций / Build → Configuration Manager</b>.')
step(2, 'Выберите <b>Сборка → Собрать решение / Build → Build Solution</b> (Ctrl+Shift+B). В окне <b>Вывод / Output</b> должен появиться успешный результат без ошибок.')
step(3, 'Откройте <b>Тест → Обозреватель тестов / Test → Test Explorer</b>, нажмите <b>Запустить все / Run All</b>. В текущем комплекте ожидаются <b>39 пройденных тестов</b>. Для тестов нужны Windows и локальная файловая система; внешние XML/PDF не используются.')
step(4, 'Для отладки можно переключиться на <b>Debug</b>. Щелкните проект <b>Internal Integration</b> правой кнопкой и выберите <b>Назначить запускаемым проектом / Set as Startup Project</b>. Не выбирайте проект тестов.')
step(5, 'Нажмите <b>F5</b> для запуска с отладчиком либо <b>Ctrl+F5</b> для запуска без отладчика. Откроется консоль, программа начнет следить за папками. Отдельного графического окна настроек у нее нет.')
step(6, 'Для проверки используйте копии файлов в отдельной тестовой входной папке. После обработки они будут удалены из входа. Пара из примера с AddressCode="39" должна появиться в выходе без изменения байтов. Если скопировать только XML, результат появится после таймаута.')
step(7, 'Для штатной остановки нажмите <b>Ctrl+C в окне консоли</b>. Кнопка остановки отладчика также завершит процесс, но может прервать его между этапами; сохраненный журнал позволит продолжить обработку.')
sub('Где найти результат обычной сборки')
p('Для Release: <b>src / InternalIntegration / bin / Release / net9.0 / InternalIntegration.exe</b>. Для Debug вместо Release используется Debug. Имя продукта в Visual Studio: <b>Internal Integration</b>; техническое имя исполняемого файла и пространства имен: <b>InternalIntegration</b>.')
p('Обычная сборка требует совместимого установленного .NET Runtime и сопутствующих файлов. Для переноса на компьютер без Runtime используйте публикацию со следующей страницы.', 'SmallRU')

page(); heading('4. Получить комплект для переноса')
p('В проект добавлен профиль <b>WindowsFolder</b>. Он публикует автономную Windows x64-версию: среда .NET входит в комплект. Это папка файлов с главным InternalIntegration.exe, а не единственный самодостаточный файл.')
step(1, 'Щелкните правой кнопкой <b>проект Internal Integration</b> и выберите <b>Опубликовать / Publish</b>. Выберите существующий профиль <b>WindowsFolder</b>. Если мастер предлагает создать профиль, выберите <b>Папка / Folder</b> и завершите мастер.')
step(2, 'Откройте <b>Показать все параметры / Show all settings</b> либо <b>Изменить / Edit</b> в выбранном профиле. Проверьте значения:')
table([['Параметр', 'Значение'],['Configuration / Конфигурация','Release'],['Target framework / Целевая платформа','net9.0'],['Deployment mode / Режим развертывания','Self-contained / Автономное'],['Target runtime / Целевая среда выполнения','win-x64'],['Produce single file / Один файл','Выключено'],['Trim unused code / Удаление неиспользуемого кода','Выключено'],['Target location / Целевая папка','publish / win-x64 в корне решения']], [290,209])
step(3, 'Нажмите <b>Опубликовать / Publish</b>. При первой автономной публикации Visual Studio может загрузить дополнительные пакеты среды выполнения. Дождитесь сообщения об успешном завершении [3].')
step(4, 'Откройте целевую папку из окна публикации. Проверьте наличие <b>InternalIntegration.exe</b>, <b>appsettings.json</b> и остальных библиотек. Переносите <b>всю папку</b> на целевой компьютер через Проводник.')
step(5, 'На целевом компьютере настройте его локальные пути в appsettings.json. Не переносите журналы State с другой рабочей системы как шаблон. Для пробного консольного запуска откройте InternalIntegration.exe двойным щелчком; для просмотра и отладки ошибок удобнее F5 на компьютере разработки.')
p('Профиль сохранен в src / InternalIntegration / Properties / PublishProfiles / WindowsFolder.pubxml. Компьютеру сборки нужен SDK; целевой Windows x64-машине для автономного комплекта отдельно устанавливать Runtime не требуется.', 'SmallRU')

page(); heading('5. Эксплуатация и устранение ошибок')
table([['Ситуация', 'Действие через интерфейс'],['Не поддерживается net9.0','Проверьте версию Visual Studio и установку .NET 9 SDK через Installer.'],['Не восстанавливаются пакеты','Проверьте интернет и источник nuget.org в Сервис → Параметры → Диспетчер пакетов NuGet → Источники пакетов.'],['Ошибка доступа или занята входная папка','Проверьте права на Input/Output/State и отсутствие второго экземпляра или уже работающей службы.'],['XML остается на входе','Проверьте готовность файлов, ожидание PDF, журнал ошибок и совпадающие имена в Output.'],['После перезапуска найден частичный результат','Сохраните State и .staging: программа продолжит собственную транзакцию; чужой файл считается конфликтом.']], [185,314])
sub('Гарантии и ограничения')
p('Результаты сначала пишутся во временные файлы. Публикуется PDF, затем XML. Исходники удаляются только после проверки результатов. Публикация двух файлов не атомарна: потребитель должен ориентироваться на появление XML. Поврежденный XML не удаляется, DTD и внешние сущности запрещены. Форматирование измененного XML может нормализоваться.')
sub('Режим Windows Service')
p('Сборка или кнопка Publish <b>не регистрирует службу Windows</b>. Для установки нужен отдельный административный шаг; этот проект не содержит графического установщика службы. Данная инструкция заканчивается получением готового комплекта через Visual Studio. Регистрация описана в README для администратора. Уже зарегистрированной службой управляют через оснастку «Службы». После переименования техническое имя службы и источника Event Log: <b>InternalIntegration</b>; прежняя регистрация автоматически не мигрирует.')
p('В консоли видны сообщения обработки. В режиме службы используется журнал Windows «Приложение», источник InternalIntegration; источник и права должны быть подготовлены администратором. Содержимое XML/PDF и данные пациентов в журнал не выводятся. Запуск под SCM в среде разработки не проверялся.', 'SmallRU')
sub('Официальные справочные материалы Microsoft')
p('[1] <link href="https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/9.0/version-requirements" color="#087F8C">Совместимость .NET 9 SDK и Visual Studio</link><br/>[2] <link href="https://dotnet.microsoft.com/en-us/download/dotnet/9.0" color="#087F8C">Установщики .NET 9 SDK</link><br/>[3] <link href="https://learn.microsoft.com/en-us/dotnet/core/deploying/" color="#087F8C">Публикация .NET: автономный комплект и зависимости</link>', 'SmallRU')
p('Названия пунктов меню могут немного отличаться в зависимости от языка и обновления Visual Studio. Английские подписи приведены для поиска соответствующих элементов.', 'SmallRU')

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
