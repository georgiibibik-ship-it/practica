-- МедТест. Модуль 1: запросы к базе данных.
-- Те же данные, что показывает приложение, получены одним SQL. Выполнять в базе MedTest_Dev.
USE MedTest_Dev;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1. Справочник пациентов: номер карты, возраст, число обследований и дата последнего.
SELECT p.CardNumber AS [Карта],
       p.LastName + N' ' + p.FirstName + ISNULL(N' ' + p.MiddleName, N'') AS [Пациент],
       DATEDIFF(year, p.BirthDate, GETDATE()) - CASE WHEN DATEADD(year, DATEDIFF(year, p.BirthDate, GETDATE()), p.BirthDate) > GETDATE() THEN 1 ELSE 0 END AS [Возраст],
       CASE p.Sex WHEN 'M' THEN N'М' ELSE N'Ж' END AS [Пол],
       COUNT(e.Id) AS [Обследований],
       CONVERT(varchar(10), MAX(e.TakenAt), 104) AS [Последний осмотр],
       CASE WHEN p.IsArchived = 1 THEN N'в архиве' ELSE N'наблюдается' END AS [Состояние]
FROM dbo.Patients p
LEFT JOIN dbo.Examinations e ON e.PatientId = p.Id
GROUP BY p.Id, p.CardNumber, p.LastName, p.FirstName, p.MiddleName, p.BirthDate, p.Sex, p.IsArchived
ORDER BY p.LastName, p.FirstName;
GO

-- 2. Последние значения показателей одного пациента с оценкой по справочной норме.
DECLARE @Card varchar(9) = 'PT-000001';
WITH Latest AS (
    SELECT v.IndicatorId, v.Value, e.TakenAt,
           ROW_NUMBER() OVER (PARTITION BY v.IndicatorId ORDER BY e.TakenAt DESC, e.Id DESC) AS N
    FROM dbo.ExaminationValues v
    JOIN dbo.Examinations e ON e.Id = v.ExaminationId
    JOIN dbo.Patients p ON p.Id = e.PatientId
    WHERE p.CardNumber = @Card)
SELECT i.Name AS [Показатель], FORMAT(l.Value, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU') AS [Значение], i.Unit AS [Ед.],
       CONCAT(FORMAT(i.RefMin, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU'), N'–', FORMAT(i.RefMax, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU')) AS [Норма],
       CASE WHEN l.Value > i.RefMax THEN N'выше нормы' WHEN l.Value < i.RefMin THEN N'ниже нормы' ELSE N'норма' END AS [Оценка],
       CASE WHEN l.Value > i.RefMax THEN N'+' + FORMAT(l.Value - i.RefMax, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU') WHEN l.Value < i.RefMin THEN N'−' + FORMAT(i.RefMin - l.Value, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU') ELSE N'—' END AS [Отклонение],
       CONVERT(varchar(10), l.TakenAt, 104) AS [Дата]
FROM Latest l JOIN dbo.Indicators i ON i.Id = l.IndicatorId
WHERE l.N = 1
ORDER BY i.SortOrder;
GO

-- 3. Статистика по каждому показателю: последнее значение каждого пациента,
--    среднее, разброс (стандартное отклонение), норма группы (среднее ± два разброса) и доля вне справочной нормы.
WITH Latest AS (
    SELECT v.IndicatorId, v.Value,
           ROW_NUMBER() OVER (PARTITION BY v.IndicatorId, e.PatientId ORDER BY e.TakenAt DESC, e.Id DESC) AS N
    FROM dbo.ExaminationValues v
    JOIN dbo.Examinations e ON e.Id = v.ExaminationId)
SELECT i.Name AS [Показатель],
       COUNT(*) AS [Пациентов],
       FORMAT(AVG(l.Value), 'N' + CAST(i.Decimals AS char(1)), 'ru-RU') AS [Среднее],
       N'±' + FORMAT(STDEV(l.Value), 'N' + CAST(i.Decimals AS char(1)), 'ru-RU') AS [Разброс],
       CONCAT(FORMAT(MIN(l.Value), 'N' + CAST(i.Decimals AS char(1)), 'ru-RU'), N'–', FORMAT(MAX(l.Value), 'N' + CAST(i.Decimals AS char(1)), 'ru-RU')) AS [Мин.–макс.],
       CONCAT(FORMAT(AVG(l.Value) - 2 * STDEV(l.Value), 'N' + CAST(i.Decimals AS char(1)), 'ru-RU'), N'–', FORMAT(AVG(l.Value) + 2 * STDEV(l.Value), 'N' + CAST(i.Decimals AS char(1)), 'ru-RU')) AS [Норма группы],
       CONCAT(FORMAT(i.RefMin, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU'), N'–', FORMAT(i.RefMax, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU')) AS [Справочная норма],
       FORMAT(100.0 * SUM(CASE WHEN l.Value < i.RefMin OR l.Value > i.RefMax THEN 1 ELSE 0 END) / COUNT(*), 'N0', 'ru-RU') + N' %' AS [Вне нормы]
FROM Latest l JOIN dbo.Indicators i ON i.Id = l.IndicatorId
WHERE l.N = 1
GROUP BY i.Id, i.Name, i.RefMin, i.RefMax, i.Decimals, i.SortOrder
ORDER BY i.SortOrder;
GO

-- 4. Пациенты, у которых последнее значение хотя бы одного показателя вне нормы (фильтр «С отклонениями»).
WITH Latest AS (
    SELECT e.PatientId, v.IndicatorId, v.Value,
           ROW_NUMBER() OVER (PARTITION BY e.PatientId, v.IndicatorId ORDER BY e.TakenAt DESC, e.Id DESC) AS N
    FROM dbo.ExaminationValues v JOIN dbo.Examinations e ON e.Id = v.ExaminationId)
SELECT p.CardNumber AS [Карта], p.LastName + N' ' + p.FirstName AS [Пациент],
       COUNT(*) AS [Вне нормы],
       STRING_AGG(i.Name, N', ') WITHIN GROUP (ORDER BY i.SortOrder) AS [Показатели]
FROM Latest l
JOIN dbo.Indicators i ON i.Id = l.IndicatorId
JOIN dbo.Patients p ON p.Id = l.PatientId
WHERE l.N = 1 AND (l.Value < i.RefMin OR l.Value > i.RefMax) AND p.IsArchived = 0
GROUP BY p.CardNumber, p.LastName, p.FirstName
ORDER BY COUNT(*) DESC, p.LastName;
GO

-- 5. Справочник показателей: нормы и допустимый диапазон ввода по группам.
SELECT i.GroupName AS [Группа], i.Name AS [Показатель], i.Code AS [Код], i.Unit AS [Ед.],
       CONCAT(FORMAT(i.RefMin, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU'), N'–', FORMAT(i.RefMax, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU')) AS [Норма],
       CONCAT(FORMAT(i.ValidMin, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU'), N'–', FORMAT(i.ValidMax, 'N' + CAST(i.Decimals AS char(1)), 'ru-RU')) AS [Допустимо при вводе],
       CASE WHEN i.IsActive = 1 THEN N'используется' ELSE N'отключён' END AS [Состояние]
FROM dbo.Indicators i
ORDER BY i.SortOrder;
GO

-- 6. Проверка ограничений: эти вставки отклоняются самой базой данных.
BEGIN TRY
    INSERT dbo.Patients(LastName, FirstName, BirthDate, Sex, CreatedBy) VALUES (N'Проверка', N'Дата', '18991231', 'F', (SELECT MIN(Id) FROM dbo.Users));
END TRY
BEGIN CATCH
    SELECT N'Дата рождения раньше 1900 года' AS [Проверка], ERROR_MESSAGE() AS [Ответ базы данных];
END CATCH;
BEGIN TRY
    INSERT dbo.Indicators(Code, GroupName, Name, Unit, RefMin, RefMax, ValidMin, ValidMax, Decimals, SortOrder)
    VALUES ('BAD', N'Проверка', N'Норма вне диапазона ввода', N'ед.', 10, 500, 0, 100, 0, 999);
END TRY
BEGIN CATCH
    SELECT N'Норма шире допустимого диапазона' AS [Проверка], ERROR_MESSAGE() AS [Ответ базы данных];
END CATCH;
GO
