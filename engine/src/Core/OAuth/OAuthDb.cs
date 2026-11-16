using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Knowledge;

// Only OpenIddict's protocol state uses EF. The knowledge engine continues to use its existing checked SQL.
// Tables are created by the normal numbered migration, never by the serving process.
public sealed class OAuthDb(DbContextOptions<OAuthDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.UseOpenIddict();
        model.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("oauth_applications");
        model.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("oauth_authorizations");
        model.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("oauth_scopes");
        model.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("oauth_tokens");
    }
}
