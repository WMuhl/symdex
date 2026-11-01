namespace Symdex.Tests;

[CollectionDefinition(Name)]
public sealed class SampleRepoCollection : ICollectionFixture<RestoredSampleRepoFixture>
{
    public const string Name = "sample-repo";
}

/// <summary>One restored copy of tests/fixtures/sample-repo for the whole collection.
/// Tests that mutate the tree call <see cref="CreateIsolatedCopy"/>.</summary>
public sealed class RestoredSampleRepoFixture : IDisposable
{
    public string Root { get; }

    public RestoredSampleRepoFixture()
    {
        Root = TestUtil.CopyFixture("sample-repo");
        TestUtil.Restore(Path.Combine(Root, "SampleRepo.sln"));
    }

    public string CreateIsolatedCopy() => TestUtil.CopyTree(Root);

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
    }
}
