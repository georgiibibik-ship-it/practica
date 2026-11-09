@echo off
chcp 65001 >nul
rem МедТест. Модуль 1: создаёт базу MedTest_Dev на LocalDB и добавляет примеры данных.
rem Нужны SQL Server Express LocalDB и sqlcmd (ставятся вместе с Visual Studio или SQL Server Management Studio).
cd /d "%~dp0"
set SERVER=(localdb)\MSSQLLocalDB
sqllocaldb start MSSQLLocalDB >nul 2>&1
echo Создание базы...
sqlcmd -S "%SERVER%" -E -I -b -f 65001 -i "01_создание_базы.sql" || goto :error
echo Примеры данных...
sqlcmd -S "%SERVER%" -E -I -b -f 65001 -i "02_примеры_данных.sql" || goto :error
echo.
echo Готово. Запросы для проверки: 03_запросы.sql
pause
exit /b 0
:error
echo.
echo Не удалось выполнить скрипт. Проверьте, что установлены LocalDB и sqlcmd.
pause
exit /b 1
