using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Microsoft.CodeAnalysis.Text;

namespace MongoZen.SourceGenerator.Tests
{
    public class DbContextSessionsGeneratorTests
    {
        [Fact]
        public async Task GeneratesSessionForDbContext()
        {
            var someDbContextDefinition = @"
            using MongoZen;

            public class Blog {}
            public class Post {}

            public class BloggingContext : DbContext
            {
                public BloggingContext(DbContextOptions options) : base(options) {}

                public IDbSet<Blog> Blogs { get; set; }
                public IDbSet<Post> Posts { get; set; }
            }";

            var expected =
                @"// <auto-generated/>
#nullable enable
using System.Threading.Tasks;
using MongoZen;

public sealed class BloggingContextSession : MongoZen.DbContextSession<BloggingContext>
{
    public BloggingContextSession(BloggingContext dbContext) : base(dbContext)
    {
        Blogs = new MongoZen.MutableDbSet<Blog>(_dbContext.Blogs, _dbContext.Options.Conventions);
        Posts = new MongoZen.MutableDbSet<Post>(_dbContext.Posts, _dbContext.Options.Conventions);
    }

    public MongoZen.IMutableDbSet<Blog> Blogs { get; }
    public MongoZen.IMutableDbSet<Post> Posts { get; }

    public async ValueTask SaveChangesAsync()
    {
        await Blogs.CommitAsync();
        await Posts.CommitAsync();
    }
}
";

            var test = new CSharpSourceGeneratorTest<SourceGenerator.DbContextSessionsGenerator, XUnitVerifier>
            {
                TestState =
                {
                    Sources = { someDbContextDefinition },
                    ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                    GeneratedSources =
                    {
                        (typeof(SourceGenerator.DbContextSessionsGenerator), "BloggingContextSession.g.cs",
                            SourceText.From(expected, Encoding.UTF8)),
                    },
                },
            };

            test.TestState.AdditionalReferences.Add(
                MetadataReference.CreateFromFile(typeof(DbContext).Assembly.Location));

            // act & assert
            await test.RunAsync();
        }
    }
}