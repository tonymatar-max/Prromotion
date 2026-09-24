-- Nexus Promotions (APE) — MS SQL, script 3 of 5: document validation, called from SBO_SP_TransactionNotification.
--
-- Rules (PRD 6.1, 6.5, FR-18, FR-43, FR-45):
--   71001  Delivery/invoice copied from a sales order whose promotions are still queued (Mode B).
--   Documents that carry no promotion data (no U_APE_Hash, no U_APE_Promo) pass.
--   Quotation/order with promotion lines but U_APE_Hash cleared, Mode B on: passes; APE_Enqueue queues it.
--   Stored hash = recomputed hash: fresh evaluation, then line checks:
--     71003  free line without 100% discount
--     71004  DiscPrcnt does not match U_APE_DiscAmt
--     71005  unknown promotion code
--   Hash differs (or is missing): allowed only when every promotion line was copied from a base document
--   unchanged (partial quantities are fine, FR-18):
--     71002  a promotion line typed or changed on this document
--     71006  copied line whose promotion or discount differs from its base line

CREATE OR ALTER PROCEDURE dbo.APE_ValidateDocument
    @ObjType      NVARCHAR(20),
    @TransType    NCHAR(1),
    @DocKey       NVARCHAR(255),
    @Error        INT           OUTPUT,
    @ErrorMessage NVARCHAR(200) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @Error = 0;
    SET @ErrorMessage = N'';

    IF @TransType NOT IN (N'A', N'U') OR @ObjType NOT IN (N'23', N'17', N'15', N'13') RETURN;

    -- No TRY_CAST / STRING_SPLIT: B1 company databases often run at compatibility level 100.
    DECLARE @DocEntry INT = CASE WHEN LEN(@DocKey) BETWEEN 1 AND 9 AND @DocKey NOT LIKE N'%[^0-9]%' THEN CAST(@DocKey AS INT) END;
    DECLARE @HeaderTable SYSNAME, @LineTable SYSNAME;
    SELECT @HeaderTable = HeaderTable, @LineTable = LineTable FROM dbo.APE_DocTables(@ObjType);
    IF @DocEntry IS NULL OR @LineTable IS NULL RETURN;

    DECLARE @ModeB     NCHAR(1) = ISNULL((SELECT Value FROM dbo.APE_Settings WHERE Name = N'ModeB'), N'N');
    DECLARE @TolText   NVARCHAR(200) = (SELECT Value FROM dbo.APE_Settings WHERE Name = N'DiscTolerance');
    DECLARE @Tolerance DECIMAL(19,6) = CASE WHEN ISNUMERIC(@TolText) = 1 THEN CAST(@TolText AS DECIMAL(19,6)) ELSE 0.01 END;
    -- B1 rounds DiscPrcnt to the company's percent decimals (often 2), so a line may be off by half a step of that.
    DECLARE @PctDec INT = 6;
    SELECT @PctDec = PercentDec FROM dbo.OADM;
    DECLARE @HalfPctStep DECIMAL(19,10) = 0.5 * POWER(CAST(10 AS DECIMAL(19,10)), -@PctDec);

    -- Text columns use the company database's collation: tempdb usually has the server's (CP1),
    -- B1 databases CP850, and mixing them fails with a collation conflict.
    CREATE TABLE #L
    (
        LineNum   INT            NOT NULL,
        Row       INT            NOT NULL,   -- row number the user sees
        Quantity  DECIMAL(19,6)  NULL,
        Price     DECIMAL(19,6)  NULL,
        DiscPrcnt DECIMAL(19,6)  NULL,
        BaseType  INT            NULL,
        BaseEntry INT            NULL,
        BaseLine  INT            NULL,
        Promo     NVARCHAR(254)  COLLATE DATABASE_DEFAULT NOT NULL,
        DiscAmt   DECIMAL(19,6)  NOT NULL,
        IsFree    NCHAR(1)       COLLATE DATABASE_DEFAULT NOT NULL,
        BaseFound BIT            NOT NULL DEFAULT (0),
        BaseDisc  DECIMAL(19,6)  NULL,
        BasePromo NVARCHAR(254)  COLLATE DATABASE_DEFAULT NULL,
        BaseFree  NCHAR(1)       COLLATE DATABASE_DEFAULT NULL
    );

    DECLARE @Sql NVARCHAR(MAX) = N'
        INSERT #L (LineNum, Row, Quantity, Price, DiscPrcnt, BaseType, BaseEntry, BaseLine, Promo, DiscAmt, IsFree)
        SELECT LineNum, VisOrder + 1, Quantity, PriceBefDi, DiscPrcnt, BaseType, BaseEntry, BaseLine,
               ISNULL(U_APE_Promo, N''''), ISNULL(U_APE_DiscAmt, 0), CASE WHEN U_APE_Free = N''Y'' THEN N''Y'' ELSE N''N'' END
        FROM dbo.' + QUOTENAME(@LineTable) + N' WHERE DocEntry = @DocEntry;
        SELECT @StoredHash = U_APE_Hash FROM dbo.' + QUOTENAME(@HeaderTable) + N' WHERE DocEntry = @DocEntry;';
    DECLARE @StoredHash NVARCHAR(64);
    EXEC sys.sp_executesql @Sql, N'@DocEntry INT, @StoredHash NVARCHAR(64) OUTPUT', @DocEntry = @DocEntry, @StoredHash = @StoredHash OUTPUT;

    -- 71001: nothing ships or gets invoiced before Mode B has applied the order's promotions (FR-43).
    IF @ObjType IN (N'15', N'13') AND @TransType = N'A'
    BEGIN
        DECLARE @PendingOrder INT =
        (
            SELECT TOP (1) Q.DocEntry
            FROM #L
            JOIN dbo.APE_Queue AS Q ON Q.ObjType = N'17' AND Q.DocEntry = #L.BaseEntry AND Q.Status IN (N'New', N'Processing')
            WHERE #L.BaseType = 17
        );
        IF @PendingOrder IS NOT NULL
        BEGIN
            SET @Error = 71001;
            SET @ErrorMessage = CONCAT(N'APE: promotions are still being applied to sales order ',
                ISNULL((SELECT CAST(DocNum AS NVARCHAR(20)) FROM dbo.ORDR WHERE DocEntry = @PendingOrder), CAST(@PendingOrder AS NVARCHAR(20))),
                N'. Try again in a few seconds.');
            RETURN;
        END
    END

    -- No promotion data at all: nothing to check.
    IF @StoredHash IS NULL AND NOT EXISTS (SELECT 1 FROM #L WHERE Promo <> N'') RETURN;

    -- Hash cleared on a quotation/order: a request to re-apply promotions in Mode B.
    IF @StoredHash IS NULL AND @ObjType IN (N'23', N'17') AND @ModeB = N'Y' RETURN;

    DECLARE @Hash CHAR(64);
    EXEC dbo.APE_ComputeDocHash @ObjType, @DocEntry, @Hash OUTPUT;
    DECLARE @Row INT, @Code NVARCHAR(50);

    IF @StoredHash IS NULL OR @StoredHash <> @Hash
    BEGIN
        -- Only unchanged copies of base-document lines may keep their promotion without a matching hash.
        -- The hash says that something changed, not which row, so the message names no row.
        IF EXISTS (SELECT 1 FROM #L
                   WHERE (Promo <> N'' OR DiscAmt <> 0 OR IsFree = N'Y') AND (BaseType IS NULL OR BaseType = -1))
        BEGIN
            SET @Error = 71002;
            SET @ErrorMessage = N'APE: promotion lines were changed after promotions were applied. Re-apply promotions.';
            RETURN;
        END

        DECLARE @BaseType INT, @BaseLines SYSNAME;
        DECLARE base_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT DISTINCT BaseType FROM #L WHERE Promo <> N'' AND BaseType > 0;
        OPEN base_cursor;
        FETCH NEXT FROM base_cursor INTO @BaseType;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @BaseLines = (SELECT LineTable FROM dbo.APE_DocTables(CAST(@BaseType AS NVARCHAR(20))));
            IF @BaseLines IS NOT NULL
            BEGIN
                SET @Sql = N'
                    UPDATE L SET BaseFound = 1, BaseDisc = B.DiscPrcnt, BasePromo = ISNULL(B.U_APE_Promo, N''''),
                                 BaseFree = CASE WHEN B.U_APE_Free = N''Y'' THEN N''Y'' ELSE N''N'' END
                    FROM #L AS L
                    JOIN dbo.' + QUOTENAME(@BaseLines) + N' AS B ON B.DocEntry = L.BaseEntry AND B.LineNum = L.BaseLine
                    WHERE L.BaseType = @BaseType;';
                EXEC sys.sp_executesql @Sql, N'@BaseType INT', @BaseType = @BaseType;
            END
            FETCH NEXT FROM base_cursor INTO @BaseType;
        END
        CLOSE base_cursor;
        DEALLOCATE base_cursor;

        SELECT TOP (1) @Row = Row FROM #L
        WHERE Promo <> N''
          AND (BaseFound = 0 OR BasePromo <> Promo OR BaseFree <> IsFree OR ABS(BaseDisc - DiscPrcnt) > @HalfPctStep)
        ORDER BY Row;
        IF @Row IS NOT NULL
        BEGIN
            SET @Error = 71006;
            SET @ErrorMessage = CONCAT(N'APE: row ', @Row, N' promotion or discount differs from the base document. Re-apply promotions.');
            RETURN;
        END
    END

    -- 71003: a free line is 100% discounted.
    SELECT TOP (1) @Row = Row FROM #L WHERE IsFree = N'Y' AND DiscPrcnt <> 100 ORDER BY Row;
    IF @Row IS NOT NULL
    BEGIN
        SET @Error = 71003;
        SET @ErrorMessage = CONCAT(N'APE: free row ', @Row, N' must have a 100% discount.');
        RETURN;
    END

    -- 71004: DiscPrcnt is not in the hash, so check it gives the promotion amount.
    SELECT TOP (1) @Row = Row FROM #L
    WHERE ABS(Quantity * Price * DiscPrcnt / 100 - DiscAmt) > @Tolerance + ABS(Quantity * Price) * @HalfPctStep / 100
      AND (Promo <> N'' OR DiscAmt <> 0)
      AND (BaseType IS NULL OR BaseType = -1)     -- copied lines carry the base's full amount (FR-18)
    ORDER BY Row;
    IF @Row IS NOT NULL
    BEGIN
        SET @Error = 71004;
        SET @ErrorMessage = CONCAT(N'APE: discount on row ', @Row, N' does not match the promotion amount. Re-apply promotions.');
        RETURN;
    END

    -- 71005: every promotion code exists in the @APE_PROMO UDO.
    IF OBJECT_ID(N'dbo.[@APE_PROMO]', N'U') IS NOT NULL
    BEGIN
        WITH Codes (Row, Code, Rest) AS
        (
            SELECT Row, CAST(N'' AS NVARCHAR(300)) COLLATE DATABASE_DEFAULT, CAST(Promo + N',' AS NVARCHAR(300)) COLLATE DATABASE_DEFAULT
            FROM #L WHERE Promo <> N''
            UNION ALL
            SELECT Row,
                   CAST(LTRIM(RTRIM(LEFT(Rest, CHARINDEX(N',', Rest) - 1))) AS NVARCHAR(300)) COLLATE DATABASE_DEFAULT,
                   CAST(SUBSTRING(Rest, CHARINDEX(N',', Rest) + 1, 300) AS NVARCHAR(300)) COLLATE DATABASE_DEFAULT
            FROM Codes WHERE Rest <> N''
        )
        SELECT TOP (1) @Row = C.Row, @Code = C.Code
        FROM Codes AS C
        WHERE C.Code <> N''
          AND NOT EXISTS (SELECT 1 FROM dbo.[@APE_PROMO] AS P WHERE P.Code = C.Code)
        ORDER BY C.Row;
        IF @Row IS NOT NULL
        BEGIN
            SET @Error = 71005;
            SET @ErrorMessage = CONCAT(N'APE: unknown promotion ', @Code, N' on row ', @Row, N'.');
            RETURN;
        END
    END
END
GO
