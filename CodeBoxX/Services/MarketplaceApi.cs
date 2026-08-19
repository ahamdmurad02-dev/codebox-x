using CodeBoxX.Models;

namespace CodeBoxX.Services;

public interface IMarketplaceCatalogSource
{
    Task<IReadOnlyList<ExtensionManifest>> GetCatalogAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalMarketplaceCatalogSource : IMarketplaceCatalogSource
{
    private readonly ExtensionMarketplaceService _marketplace;

    public LocalMarketplaceCatalogSource(ExtensionMarketplaceService marketplace)
    {
        _marketplace = marketplace;
    }

    public Task<IReadOnlyList<ExtensionManifest>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ExtensionManifest> catalog = _marketplace.Catalog.Select(package => package.Manifest).ToList();
        return Task.FromResult(catalog);
    }
}

/// <summary>
/// Transport-neutral marketplace API facade. A future authenticated HTTPS source can implement
/// IMarketplaceCatalogSource without changing marketplace UI or package validation behavior.
/// </summary>
public sealed class MarketplaceApi
{
    private readonly IMarketplaceCatalogSource _catalogSource;

    public MarketplaceApi(IMarketplaceCatalogSource catalogSource)
    {
        _catalogSource = catalogSource;
    }

    public Task<IReadOnlyList<ExtensionManifest>> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        _catalogSource.GetCatalogAsync(cancellationToken);
}
