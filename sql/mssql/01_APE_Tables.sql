-- Nexus Promotions (APE) — MS SQL, script 1 of 5: settings and queue tables.
-- Run in EACH B1 company database, after the APE setup tool has created the UDTs/UDOs/UDFs.
-- Requires SQL Server 2019+ (UTF-8 collation used by the hash).
--
-- These are plain SQL tables (like Interco_TempQueue), not B1 user tables: only the APE procedures and
-- services write to them, so they need no Code/Name columns and never go through the DI API.

IF OBJECT_ID(N'dbo.APE_Settings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.APE_Settings
    (
        Name  NVARCHAR(50)  NOT NULL CONSTRAINT PK_APE_Settings PRIMARY KEY,
        Value NVARCHAR(200) NULL
    );
END
GO

-- Defaults; existing values are never overwritten.
MERGE dbo.APE_Settings AS t
USING (VALUES
    (N'HashKey',      N''),     -- same value as Promotions:HashKey in the engine's appsettings.json
    (N'ModeA',        N'Y'),    -- Y: the add-on auto-applies promotions on Add/Update, on every workstation at once.
                                 -- N: the Apply Promotions button still works, but nothing fires automatically on save.
                                 -- Toggle from the admin app instead of editing each workstation.
    (N'ApiUrl',       NULL),    -- where the promotion API runs, e.g. http://promo-server:5190 — read by every
                                 -- workstation's add-on, so nothing is configured per machine (set with the Setup
                                 -- tool: "setting ApiUrl http://promo-server:5190"). Blank: http://localhost:5190
    (N'ApiKey',       NULL),    -- X-Api-Key for that API, when Promotions:ApiKeys is set on it
    (N'ModeB',        N'Y'),    -- Y: quotations/orders saved without promotions are queued for the worker
    (N'TechUserId',   NULL),    -- OUSR.USERID of the APE service user (loop guard)
    (N'DiscTolerance', N'0.01') -- allowed gap between DiscPrcnt and U_APE_DiscAmt, in document currency
) AS s (Name, Value)
ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES (s.Name, s.Value);
GO

-- Mode B queue (PRD 6.5, FR-40). Written by APE_Enqueue from SBO_SP_PostTransactionNotice,
-- processed by the queue worker in arrival order.
IF OBJECT_ID(N'dbo.APE_Queue', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.APE_Queue
    (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_APE_Queue PRIMARY KEY,
        ObjType     NVARCHAR(20)  NOT NULL,
        DocEntry    INT           NOT NULL,
        TransType   NCHAR(1)      NOT NULL,
        Status      NVARCHAR(12)  NOT NULL CONSTRAINT DF_APE_Queue_Status DEFAULT (N'New'), -- New / Processing / Done / Failed
        Attempts    INT           NOT NULL CONSTRAINT DF_APE_Queue_Attempts DEFAULT (0),
        LastError   NVARCHAR(MAX) NULL,
        CreatedAt   DATETIME2     NOT NULL CONSTRAINT DF_APE_Queue_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ProcessedAt DATETIME2     NULL
    );
    CREATE INDEX IX_APE_Queue_Open ON dbo.APE_Queue (Status, Id) INCLUDE (ObjType, DocEntry);
    CREATE INDEX IX_APE_Queue_Doc  ON dbo.APE_Queue (ObjType, DocEntry, Status);
END
GO

-- Document object type → header and line tables. Only sales documents take part.
CREATE OR ALTER FUNCTION dbo.APE_DocTables (@ObjType NVARCHAR(20))
RETURNS TABLE
AS
RETURN
    SELECT HeaderTable, LineTable
    FROM (VALUES
        (N'23',  N'OQUT', N'QUT1'),   -- Sales Quotation
        (N'17',  N'ORDR', N'RDR1'),   -- Sales Order
        (N'15',  N'ODLN', N'DLN1'),   -- Delivery
        (N'13',  N'OINV', N'INV1'),   -- A/R Invoice and A/R Reserve Invoice
        (N'14',  N'ORIN', N'RIN1'),   -- A/R Credit Memo
        (N'16',  N'ORDN', N'RDN1'),   -- Return
        (N'112', N'ODRF', N'DRF1')    -- Draft
    ) AS m (ObjType, HeaderTable, LineTable)
    WHERE m.ObjType = @ObjType;
GO
