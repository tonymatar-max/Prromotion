-- Nexus Promotions (APE) — MS SQL, script 5 of 5: hook into the B1 notification procedures.
-- This file is NOT run as-is. Paste each block into the existing procedure, before its final
-- "select @error, @error_message", next to the other partners' code already there.

-- ── SBO_SP_TransactionNotification ───────────────────────────────────────────────
-- Validates promotion data on quotations, orders, deliveries and A/R invoices.

    -- Nexus Promotions (APE) — BEGIN
    IF @error = 0 AND @object_type IN (N'23', N'17', N'15', N'13')
        EXEC dbo.APE_ValidateDocument
             @object_type, @transaction_type, @list_of_cols_val_tab_del,
             @error OUTPUT, @error_message OUTPUT;
    -- Nexus Promotions (APE) — END

-- ── SBO_SP_PostTransactionNotice ─────────────────────────────────────────────────
-- Queues quotations and orders saved without promotions for the Mode B worker.

    -- Nexus Promotions (APE) — BEGIN
    IF @object_type IN (N'23', N'17')
        EXEC dbo.APE_Enqueue @object_type, @transaction_type, @list_of_cols_val_tab_del;
    -- Nexus Promotions (APE) — END
