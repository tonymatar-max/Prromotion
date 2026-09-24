using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>One entry of the API's "Companies" configuration.</summary>
public sealed class CompanyOptions
{
    /// <summary>The B1 company database, exactly as the add-on reports it (X-Company-Db). Case does not matter.</summary>
    public string Db { get; set; } = "";
    /// <summary>What the admin app calls it (for example "Head Office"); defaults to <see cref="Db"/>.</summary>
    public string? Name { get; set; }
    /// <summary>The company that holds the promotions (@APE_PROMO). Exactly one; the first is used when none is marked.</summary>
    public bool Master { get; set; }
    /// <summary>To this company's dbo.APE_Settings (the central Mode A switch). Defaults to Sql:ConnectionString for a single company.</summary>
    public string? SqlConnectionString { get; set; }
    /// <summary>Must equal dbo.APE_Settings 'HashKey' in this company. Defaults to Promotions:HashKey.</summary>
    public string? HashKey { get; set; }
    public int? PriceList { get; set; }
    /// <summary>Service Layer user and password for this company; default to ServiceLayer:User and :Password.</summary>
    public string? User { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// Everything the server needs for one company: its own Service Layer session (items, customers, price lists and
/// item groups differ per company), its central settings and its hash key. Promotions are NOT per company: they are
/// read once from the master company and filtered with <see cref="CompanyScope"/>.
/// </summary>
public sealed class CompanyContext
{
    public required string Db { get; init; }
    public string? Name { get; init; }
    public bool IsMaster { get; init; }
    public string? HashKey { get; init; }
    /// <summary>Null when promotions come from files (development and demos): there is no B1 to talk to.</summary>
    public ServiceLayer? ServiceLayer { get; init; }
    public B1MasterData? MasterData { get; init; }
    /// <summary>Item data used when there is no B1 to read it from (tests, file mode); otherwise <see cref="MasterData"/> is used.</summary>
    public IItemCatalog? Catalog { get; init; }
    /// <summary>Lookups (item groups, manufacturers...) in this company, and, for the master, the @APE_PROMO editing.</summary>
    public PromotionAdmin? Admin { get; init; }
    public ApeSettings Settings { get; init; } = new(null);
    /// <summary>Evaluates one B1 document in this company; set once the store exists.</summary>
    public DocumentEvaluator? Evaluator { get; set; }

    public string Label => string.IsNullOrWhiteSpace(Name) ? Db : Name!;
}

public sealed class CompanyRegistry
{
    readonly List<CompanyContext> _all;

    public CompanyRegistry(IEnumerable<CompanyContext> companies)
    {
        _all = companies.ToList();
        if (_all.Count == 0) throw new ArgumentException("At least one company is required.", nameof(companies));

        var duplicate = _all.GroupBy(c => c.Db.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Company '{duplicate.Key}' is listed twice.");

        var masters = _all.Where(c => c.IsMaster).ToList();
        if (masters.Count > 1)
            throw new InvalidOperationException("Only one company can be the master: " + string.Join(", ", masters.Select(m => m.Db)));
        Master = masters.Count == 1 ? masters[0] : _all[0];
    }

    public IReadOnlyList<CompanyContext> All => _all;

    /// <summary>The company that holds the promotions, and the one requests without a company are answered for.</summary>
    public CompanyContext Master { get; }

    public bool IsSingle => _all.Count == 1;

    public CompanyContext? Find(string? db) =>
        string.IsNullOrWhiteSpace(db) ? null : _all.FirstOrDefault(c => CompanyScope.Same(c.Db, db));

    /// <summary>
    /// Builds the registry from configuration. Without a "Companies" section there is one company, the one in
    /// ServiceLayer:CompanyDb, and everything behaves as it did before several companies were supported.
    /// </summary>
    public static CompanyRegistry Build(IReadOnlyList<CompanyOptions> configured, ServiceLayerOptions baseSl,
        string? baseSqlConnection, string? baseHashKey, bool useServiceLayer)
    {
        var entries = configured.Count > 0
            ? configured
            : [new CompanyOptions { Db = useServiceLayer ? baseSl.CompanyDb : "default", Master = true }];

        var single = entries.Count == 1;
        var contexts = new List<CompanyContext>();
        foreach (var o in entries)
        {
            if (string.IsNullOrWhiteSpace(o.Db)) throw new InvalidOperationException("A company in \"Companies\" has no Db.");

            ServiceLayer? sl = null; B1MasterData? md = null; PromotionAdmin? admin = null;
            if (useServiceLayer)
            {
                var slOptions = new ServiceLayerOptions
                {
                    Url = baseSl.Url,
                    CompanyDb = o.Db,
                    User = o.User ?? baseSl.User,
                    Password = o.Password ?? baseSl.Password,
                    AllowSelfSignedCertificate = baseSl.AllowSelfSignedCertificate,
                    PriceList = o.PriceList ?? baseSl.PriceList,
                };
                sl = new ServiceLayer(slOptions);
                md = new B1MasterData(sl, slOptions.PriceList);
                admin = new PromotionAdmin(sl);
            }

            contexts.Add(new CompanyContext
            {
                Db = o.Db.Trim(),
                Name = o.Name,
                IsMaster = o.Master,
                HashKey = o.HashKey ?? baseHashKey,
                ServiceLayer = sl,
                MasterData = md,
                Admin = admin,
                Settings = new ApeSettings(o.SqlConnectionString ?? (single ? baseSqlConnection : null)),
            });
        }
        return new CompanyRegistry(contexts);
    }
}
