# Usage ledger and budget consumption (proposal, not built)

Status: **design for Tony to confirm.** Nothing here is implemented. It changes how budgets are counted in the customer's SAP database, so the rules below need a yes before code is written.

## What exists today

- A promotion has `U_Budget` and `U_BudgetUsed` (UDO `@APE_PROMO`). The engine already treats `Budget - BudgetUsed` as a cap (`Promotion.BudgetRemaining`, `Cap`), so a promotion stops discounting when the budget is gone.
- **Nothing ever updates `U_BudgetUsed`.** The cap therefore never tightens; budgets are effectively unlimited. That is the gap.
- A saved document only keeps the promotion codes per line (`U_APE_Promo`, comma separated) and the line discount (`U_APE_DiscAmt`), not how much each promotion gave. The engine does know it (`EvaluationResult.Promotions`: code, version, amount).

## Proposed design

1. **Ledger table** `dbo.APE_Ledger` (plain SQL table next to `APE_Queue` / `APE_Settings`, created by `Setup install`): `Id, DocType, DocEntry, PromoCode, PromoVersion, Amount, Status (Active|Reversed), CreatedAt, ReversedAt`. One row per document and promotion.
2. **Per-promotion amounts on the document.** Add a header UDF `U_APE_Usage` (text, for example `NIVEA15:1.65;SHAMPOO-B2G1:1.95`) written by every channel from the engine result (add-on, worker and the portal endpoint, which already returns `applied`). It is not part of the hash. Without it the line discount would have to be split equally between a line's codes, which is wrong whenever two promotions stack with different amounts.
3. **When a row is written:** by the existing `PostTransactionNotice` hook, in the same place Mode B enqueues, for a document that is new or updated. An update replaces the document's rows (reverse the old ones, insert the new).
4. **When budget is consumed:** on a **Sales Order** (and an A/R Invoice that has no base document, for direct invoicing), not on a Quotation. A Delivery or Invoice copied from an order carries the same codes (FR-18) and must **not** count again, so documents with a base document are skipped.
5. **When it is given back:** when the document is cancelled or closed without delivery, its rows become `Reversed`.
6. **Keeping `U_BudgetUsed` right:** a small step in the worker (or the hook) sets `U_BudgetUsed = SUM(Amount) of Active rows` for each promotion whose rows changed. The engine and the admin app then see the real remaining budget with no other change.
7. **Admin app:** the promotion screen shows used, remaining and the documents behind it (read from the ledger); a Reconcile button recomputes `U_BudgetUsed` from the ledger.

## Decisions needed

1. **Soft or hard limit?** Evaluation reads the remaining budget, so two orders saved at the same moment can both use the last of it and overshoot slightly (soft limit, the default here). A hard limit means the validation procedure refuses the save with a new error (71007) when the ledger would go over budget: stricter, but a customer or clerk gets a refusal at save time.
2. **Which documents consume?** Proposed: Sales Orders, plus A/R Invoices without a base document. Say if only invoices should count, or quotations too.
3. **Cancelled orders give the budget back?** Proposed: yes.
4. **Campaign budgets.** `U_Campaign` groups promotions. Is there ever a budget per campaign (shared by its promotions) or only per promotion? Per campaign needs the ledger to sum across the campaign.

## Not covered

- HANA: the hook and ledger are MS SQL only until the HANA version of the SQL exists (see README, "Next").
- Per-customer or per-coupon usage limits (a customer can use the coupon once) are a different counter on the same ledger and are not part of this.
