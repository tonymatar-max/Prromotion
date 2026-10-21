# Connecting a sales channel (Cobalt Customer Portal, e-commerce) to Cobalt B1 Promotions

For channels that create the B1 sales order themselves through the Service Layer. The channel sends the order it is
about to create, gets back the discounted lines and the exact Service Layer fields to post.

## Request

`POST {server}/api/v1/portal/orders/evaluate`

| Header | |
| --- | --- |
| `X-Api-Key` | Required when `Promotions:ApiKeys` is set on the server. |
| `X-Company-Db` | The B1 company database (URL-encoded). Required when the server serves several companies; the server answers 409 for a company it does not serve. |

```json
{
  "cardCode": "C20000",
  "channel": "PORTAL",
  "coupons": ["SUMMER10"],
  "docDate": "2026-10-21",
  "amountDecimals": 2,
  "lines": [
    { "lineNum": 0, "itemCode": "N-CREAM", "quantity": 4, "unitPrice": 2.75 },
    { "lineNum": 1, "itemCode": "SH-400",  "quantity": 3, "unitPrice": 1.95 }
  ]
}
```

- `unitPrice` is the price **before** promotions, in the order's currency (the customer's price from SAP).
- `lineNum` is the channel's own id, unique per line; it comes back as `sourceLineNum`.
- `channel` matches a promotion's Channel audience rule. Default `PORTAL`.
- `amountDecimals`: 3 for KWD/BHD/OMR, 2 for AED/SAR/QAR. `coupons`, `docDate` are optional.
- Up to 1000 lines. A bad request is `400 { "errors": [...] }` listing every problem.

## Response (abridged)

```json
{
  "applied": [ { "code": "NIVEA15", "name": "15% off all Nivea", "timesApplied": 1, "discountAmount": 1.65 } ],
  "lines": [
    { "sourceLineNum": 0, "itemCode": "N-CREAM", "quantity": 4, "unitPrice": 2.75, "discountPercent": 15,
      "discountAmount": 1.65, "netTotal": 9.35, "isFree": false, "isAdded": false, "promotionCodes": "NIVEA15" },
    { "sourceLineNum": 1, "itemCode": "SH-400", "quantity": 1, "unitPrice": 1.95, "discountPercent": 100,
      "discountAmount": 1.95, "netTotal": 0, "isFree": true, "isAdded": false, "promotionCodes": "SHAMPOO-B2G1" }
  ],
  "grossTotal": 12.65, "discountTotal": 3.6, "netTotal": 9.05,
  "nearMisses": [],
  "hash": "9F2C...",
  "serviceLayer": {
    "header": { "U_APE_Hash": "9F2C...", "U_APE_Mode": "A", "U_APE_Status": "Applied", "U_APE_EvalAt": "2026-10-21 10:00:00" },
    "documentLines": [
      { "ItemCode": "N-CREAM", "Quantity": 4, "UnitPrice": 2.75, "DiscountPercent": 15,
        "U_APE_Promo": "NIVEA15", "U_APE_DiscAmt": 1.65, "U_APE_Free": "N", "U_APE_Group": "" }
    ]
  }
}
```

- Show `lines`, `applied`, the totals and `nearMisses` ("add 2 more to get ...") to the customer.
- A promotion can **split a line** (the free units become their own 100% line) and **add reward lines**, so the order
  may have more lines than the cart.

## Posting the order

Build the normal `POST /Orders` body (CardCode, DocDueDate, NumAtCard, ShipToCode, Comments ...), then:

1. Copy every key of `serviceLayer.header` to the top level of the order.
2. Use `serviceLayer.documentLines` **as the order's `DocumentLines`**, exactly as returned. Do not drop the free or added lines, and do not change quantity, `UnitPrice`, `DiscountPercent` or any `U_APE_*` value.

Why exactly: SAP recomputes the hash from the item, quantity, unit price, discount amount, promotion codes and free flag of
every line (`U_APE_Hash`). A changed value, a missing free line, or lines evaluated before the cart changed gives error
**71002**. Evaluate again whenever the cart changes, and again right before posting (promotions can expire, budgets run out).
Other SAP checks that can refuse the order: 71003 (free line without 100% discount), 71004 (`DiscountPercent` does not give
`U_APE_DiscAmt`), 71005 (unknown promotion), 71006 (copied line differs from its base line).

Prices: send `UnitPrice` with the line (SAP then does not re-price it). The engine works on the price you sent, so use the
price SAP would use for that customer. Quantities with more than the item's decimals, or a unit price SAP rounds to fewer
decimals than the engine used, can break the hash; round the way SAP's price decimals do before calling.

Reward lines (promotions that add a free item) have no warehouse: SAP uses the item's default warehouse. Set
`WarehouseCode` on those lines if your order uses a different one (it is not part of the hash).

## When the promotion server is unavailable

Do not block the customer. Either post the order without promotions (SAP's Mode B worker applies them after saving, if
it is running and `ModeB` is on), or hold the order for staff. Never post `U_APE_*` fields you did not get from this call.

## Cobalt Customer Portal checklist

1. Settings: promotion server URL, API key, company database, enable switch, timeout (a few seconds).
2. Cart: call the endpoint on every change (debounced) and show the result.
3. Placement: call again, merge the `serviceLayer` fields into the Orders payload, post.
4. Log the request/response reference in the portal's SAP log.
