-- Minimal stand-in for the B1 tables the APE SQL touches. Test database only; never run in a company DB.
-- Column names and types follow B1 10 on MS SQL; UDFs are what the APE setup tool will create.

DECLARE @h NVARCHAR(10), @l NVARCHAR(10), @sql NVARCHAR(MAX);
DECLARE t CURSOR LOCAL FOR SELECT h, l FROM (VALUES ('OQUT','QUT1'),('ORDR','RDR1'),('ODLN','DLN1'),('OINV','INV1')) v(h,l);
OPEN t; FETCH NEXT FROM t INTO @h, @l;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'
    CREATE TABLE dbo.' + @h + N' (
        DocEntry INT NOT NULL PRIMARY KEY, DocNum INT NOT NULL, DocStatus NCHAR(1) NOT NULL DEFAULT (N''O''),
        UserSign SMALLINT NULL, UserSign2 SMALLINT NULL,
        U_APE_Hash NVARCHAR(64) NULL, U_APE_Status NVARCHAR(10) NULL, U_APE_Mode NVARCHAR(1) NULL);
    CREATE TABLE dbo.' + @l + N' (
        DocEntry INT NOT NULL, LineNum INT NOT NULL, VisOrder INT NOT NULL,
        ItemCode NVARCHAR(50) NULL, Quantity NUMERIC(19,6) NULL, PriceBefDi NUMERIC(19,6) NULL, DiscPrcnt NUMERIC(19,6) NULL,
        BaseType INT NULL DEFAULT (-1), BaseEntry INT NULL, BaseLine INT NULL,
        U_APE_Promo NVARCHAR(254) NULL, U_APE_Ver INT NULL, U_APE_DiscAmt NUMERIC(19,6) NULL,
        U_APE_Free NVARCHAR(1) NULL, U_APE_Group NVARCHAR(50) NULL,
        PRIMARY KEY (DocEntry, LineNum));';
    EXEC (@sql);
    FETCH NEXT FROM t INTO @h, @l;
END
CLOSE t; DEALLOCATE t;

CREATE TABLE dbo.[@APE_PROMO] (Code NVARCHAR(50) NOT NULL PRIMARY KEY, Name NVARCHAR(100) NULL, U_Status NVARCHAR(10) NULL);

-- Company settings: SBODemoHO rounds percentages to 2 decimals, like most companies.
CREATE TABLE dbo.OADM (PercentDec SMALLINT NOT NULL);
INSERT dbo.OADM (PercentDec) VALUES (2);
