using Symdex.Core;

namespace Symdex.Tests;

public sealed class IndexDatabaseTests
{
    [Fact]
    public void RefsFor_returns_empty_when_the_symbol_key_is_missing()
    {
        var root = Directory.CreateTempSubdirectory("symdex-db-").FullName;
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            Assert.Empty(db.ResolveSymbolCandidates(null));
            Assert.Empty(db.RefsFor(null, kindFilter: null, 50));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
