-- Nexus Promotions (APE) — MS SQL, script 2 of 5: result hash.
-- Must stay byte-for-byte identical to Nexus.Promotions.Engine.ResultHasher:
--   one row per line, rows sorted by binary comparison (= C# string.CompareOrdinal), "\n" after every row:
--     ItemCode|Quantity|PriceBefDi|U_APE_DiscAmt|U_APE_Promo|Y/N
--   numbers as fixed 6 decimals ("3.000000"); text UTF-8;
--   hash = uppercase hex SHA-256 of  HashKey + "\n" + rows + HashKey.

CREATE OR ALTER PROCEDURE dbo.APE_ComputeDocHash
    @ObjType  NVARCHAR(20),
    @DocEntry INT,
    @Hash     CHAR(64) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @Hash = NULL;

    DECLARE @LineTable SYSNAME = (SELECT LineTable FROM dbo.APE_DocTables(@ObjType));
    IF @LineTable IS NULL RETURN;

    DECLARE @Key  NVARCHAR(200) = ISNULL((SELECT Value FROM dbo.APE_Settings WHERE Name = N'HashKey'), N'');
    DECLARE @Rows NVARCHAR(MAX);

    -- FOR XML PATH, not STRING_AGG: STRING_AGG ... WITHIN GROUP does not compile at compatibility level 100.
    -- ", TYPE).value(...)" turns the XML back into plain text, so "&", "<" and the newlines survive unescaped.
    DECLARE @Sql NVARCHAR(MAX) = N'
        SELECT @Rows = (
            SELECT R.Txt AS [text()]
            FROM dbo.' + QUOTENAME(@LineTable) + N' AS L
            CROSS APPLY (SELECT
                  ISNULL(L.ItemCode, N'''') + N''|''
                + CONVERT(NVARCHAR(40), CAST(L.Quantity AS DECIMAL(19,6))) + N''|''
                + CONVERT(NVARCHAR(40), CAST(L.PriceBefDi AS DECIMAL(19,6))) + N''|''
                + CONVERT(NVARCHAR(40), CAST(ISNULL(L.U_APE_DiscAmt, 0) AS DECIMAL(19,6))) + N''|''
                + ISNULL(L.U_APE_Promo, N'''') + N''|''
                + CASE WHEN L.U_APE_Free = N''Y'' THEN N''Y'' ELSE N''N'' END
                + NCHAR(10) AS Txt) AS R
            WHERE L.DocEntry = @DocEntry
            ORDER BY R.Txt COLLATE Latin1_General_BIN2
            FOR XML PATH(''''), TYPE).value(''.'', ''NVARCHAR(MAX)'');';

    EXEC sys.sp_executesql @Sql, N'@DocEntry INT, @Rows NVARCHAR(MAX) OUTPUT', @DocEntry = @DocEntry, @Rows = @Rows OUTPUT;

    DECLARE @Text NVARCHAR(MAX) = @Key + NCHAR(10) + ISNULL(@Rows, N'') + @Key;

    -- Converting to VARCHAR under a UTF-8 collation gives the UTF-8 bytes the engine hashes.
    SET @Hash = CONVERT(CHAR(64),
        HASHBYTES('SHA2_256', CAST(@Text COLLATE Latin1_General_100_CI_AS_SC_UTF8 AS VARCHAR(MAX))), 2);
END
GO
