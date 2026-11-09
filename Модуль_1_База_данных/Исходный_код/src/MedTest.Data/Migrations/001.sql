-- МедТест: исходная схема. Время обследований хранится по местному времени клиники.
CREATE TABLE dbo.SchemaMigrations(
    Version int PRIMARY KEY,
    AppliedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());

CREATE TABLE dbo.Users(
    Id int IDENTITY PRIMARY KEY,
    Login varchar(32) COLLATE Latin1_General_100_CI_AS NOT NULL CONSTRAINT UX_Users_Login UNIQUE,
    FullName nvarchar(150) NOT NULL,
    Role varchar(10) NOT NULL CONSTRAINT CK_Users_Role CHECK (Role IN ('Admin','Doctor')),
    IsActive bit NOT NULL DEFAULT 1,
    PasswordHash varbinary(32) NOT NULL,
    Salt varbinary(32) NOT NULL,
    Iterations int NOT NULL CONSTRAINT CK_Users_Iterations CHECK (Iterations >= 100000),
    CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());

CREATE TABLE dbo.Patients(
    Id int IDENTITY PRIMARY KEY,
    CardNumber AS ('PT-' + RIGHT('000000' + CAST(Id AS varchar(10)), 6)) PERSISTED,
    LastName nvarchar(60) NOT NULL,
    FirstName nvarchar(60) NOT NULL,
    MiddleName nvarchar(60) NULL,
    BirthDate date NOT NULL CONSTRAINT CK_Patients_Birth CHECK (BirthDate >= '19000101'),
    Sex char(1) NOT NULL CONSTRAINT CK_Patients_Sex CHECK (Sex IN ('M','F')),
    Phone varchar(16) NULL,
    Note nvarchar(1000) NULL,
    IsArchived bit NOT NULL DEFAULT 0,
    CreatedBy int NOT NULL REFERENCES dbo.Users(Id),
    CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Patients_Names CHECK (LEN(LastName) > 0 AND LEN(FirstName) > 0));
CREATE INDEX IX_Patients_Name ON dbo.Patients(LastName, FirstName);

CREATE TABLE dbo.Indicators(
    Id int IDENTITY PRIMARY KEY,
    Code varchar(16) NOT NULL CONSTRAINT UX_Indicators_Code UNIQUE,
    GroupName nvarchar(60) NOT NULL,
    Name nvarchar(100) NOT NULL,
    Unit nvarchar(30) NOT NULL,
    RefMin decimal(12,3) NOT NULL,
    RefMax decimal(12,3) NOT NULL,
    ValidMin decimal(12,3) NOT NULL,
    ValidMax decimal(12,3) NOT NULL,
    Decimals tinyint NOT NULL CONSTRAINT CK_Indicators_Decimals CHECK (Decimals <= 3),
    SortOrder int NOT NULL,
    IsActive bit NOT NULL DEFAULT 1,
    CONSTRAINT CK_Indicators_Ref CHECK (RefMin <= RefMax),
    CONSTRAINT CK_Indicators_Valid CHECK (ValidMin < ValidMax AND ValidMin <= RefMin AND ValidMax >= RefMax));

CREATE TABLE dbo.Examinations(
    Id int IDENTITY PRIMARY KEY,
    PatientId int NOT NULL REFERENCES dbo.Patients(Id),
    DoctorId int NOT NULL REFERENCES dbo.Users(Id),
    TakenAt datetime2(0) NOT NULL,
    Comment nvarchar(1000) NULL,
    CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt datetime2 NULL);
CREATE INDEX IX_Examinations_Patient ON dbo.Examinations(PatientId, TakenAt);

CREATE TABLE dbo.ExaminationValues(
    ExaminationId int NOT NULL REFERENCES dbo.Examinations(Id) ON DELETE CASCADE,
    IndicatorId int NOT NULL REFERENCES dbo.Indicators(Id),
    Value decimal(12,3) NOT NULL,
    CONSTRAINT PK_ExaminationValues PRIMARY KEY (ExaminationId, IndicatorId));
CREATE INDEX IX_ExaminationValues_Indicator ON dbo.ExaminationValues(IndicatorId) INCLUDE (Value);

CREATE TABLE dbo.AuditLog(
    Id bigint IDENTITY PRIMARY KEY,
    At datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UserId int NULL REFERENCES dbo.Users(Id),
    Action varchar(40) NOT NULL,
    Entity varchar(20) NOT NULL,
    EntityId int NULL,
    Details nvarchar(1000) NULL);

-- Справочные нормы для взрослых.
INSERT dbo.Indicators(Code, GroupName, Name, Unit, RefMin, RefMax, ValidMin, ValidMax, Decimals, SortOrder) VALUES
('SBP',  N'Давление и пульс',   N'Систолическое давление',  N'мм рт. ст.', 90,   139,  50,  260, 0, 10),
('DBP',  N'Давление и пульс',   N'Диастолическое давление', N'мм рт. ст.', 60,   89,   30,  160, 0, 20),
('HR',   N'Давление и пульс',   N'Пульс',                   N'уд/мин',     60,   90,   25,  250, 0, 30),
('HGB',  N'Общий анализ крови', N'Гемоглобин',              N'г/л',        120,  160,  30,  250, 0, 40),
('RBC',  N'Общий анализ крови', N'Эритроциты',              N'млн/мкл',     3.8,  5.5,  1,   9,   2, 50),
('WBC',  N'Общий анализ крови', N'Лейкоциты',               N'тыс/мкл',     4.0,  9.0,  0.5, 60,  1, 60),
('PLT',  N'Общий анализ крови', N'Тромбоциты',              N'тыс/мкл',     180,  320,  10,  1500, 0, 70),
('ESR',  N'Общий анализ крови', N'СОЭ',                     N'мм/ч',       2,    15,   0,   150, 0, 80),
('GLU',  N'Биохимия крови',     N'Глюкоза',                 N'ммоль/л',    3.3,  5.5,  1,   40,  1, 90),
('CHOL', N'Биохимия крови',     N'Общий холестерин',        N'ммоль/л',    3.0,  5.2,  1,   20,  1, 100),
('CREA', N'Биохимия крови',     N'Креатинин',               N'мкмоль/л',   62,   115,  20,  1500, 0, 110);
