using System.Text.RegularExpressions;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Guide;

/// <summary>
/// Finds titles like the ones given, from what each title is: its genres, keywords, people, studio,
/// decade and description. Each of those is weighted by how rare it is in the library (TF-IDF), so
/// sharing an unusual keyword counts for much more than sharing "Drama". No model or download needed.
/// </summary>
public sealed partial class SimilarityIndex
{
    private const double MinimumScore = 0.04;

    // Common words that say nothing about what a title is about.
    private static readonly HashSet<string> StopWords =
    [
        "about", "after", "again", "against", "also", "back", "been", "before", "being", "between", "both",
        "comes", "could", "does", "down", "during", "each", "even", "ever", "every", "find", "finds", "first",
        "from", "gets", "give", "going", "have", "help", "here", "himself", "herself", "into", "just", "know",
        "life", "lives", "like", "long", "look", "made", "make", "makes", "many", "more", "most", "much", "must",
        "never", "only", "other", "over", "people", "same", "should", "show", "since", "some", "soon", "still",
        "story", "such", "take", "takes", "than", "that", "their", "them", "themselves", "then", "there", "these",
        "they", "thing", "things", "this", "those", "though", "through", "time", "together", "turn", "turns",
        "under", "until", "upon", "very", "want", "wants", "well", "were", "what", "when", "where", "which",
        "while", "with", "within", "without", "world", "would", "year", "years", "your", "series", "film", "movie",
    ];

    private readonly Dictionary<string, Dictionary<string, double>> _vectors;

    private SimilarityIndex(Dictionary<string, Dictionary<string, double>> vectors) => _vectors = vectors;

    public static SimilarityIndex Build(IReadOnlyList<CatalogTitle> titles)
    {
        var features = titles.ToDictionary(t => t.Id, Features);

        var documentFrequency = new Dictionary<string, int>();
        foreach (var key in features.Values.SelectMany(f => f.Keys))
        {
            documentFrequency[key] = documentFrequency.GetValueOrDefault(key) + 1;
        }

        var vectors = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (id, weights) in features)
        {
            var vector = new Dictionary<string, double>();
            foreach (var (key, weight) in weights)
            {
                // A feature every title has (ln 1 = 0) contributes nothing.
                var idf = Math.Log((1.0 + titles.Count) / (1.0 + documentFrequency[key]));
                if (idf > 0)
                {
                    vector[key] = weight * idf;
                }
            }

            var length = Math.Sqrt(vector.Values.Sum(v => v * v));
            if (length > 0)
            {
                foreach (var key in vector.Keys.ToList())
                {
                    vector[key] /= length;
                }
            }

            vectors[id] = vector;
        }

        return new SimilarityIndex(vectors);
    }

    /// <summary>Titles most like all of the seeds, best first, leaving out the seeds themselves.</summary>
    public IReadOnlyList<(string Id, double Score)> MoreLike(IReadOnlyCollection<string> seedIds, int count)
    {
        var seeds = seedIds.Where(_vectors.ContainsKey).Select(id => _vectors[id]).ToList();
        if (seeds.Count == 0)
        {
            return [];
        }

        var seedSet = seedIds.ToHashSet();
        return _vectors
            .Where(p => !seedSet.Contains(p.Key))
            .Select(p => (Id: p.Key, Score: seeds.Average(seed => Cosine(seed, p.Value))))
            .Where(p => p.Score >= MinimumScore)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var sum = 0.0;
        foreach (var (key, value) in small)
        {
            if (large.TryGetValue(key, out var other))
            {
                sum += value * other;
            }
        }

        return sum;
    }

    private static Dictionary<string, double> Features(CatalogTitle title)
    {
        var features = new Dictionary<string, double>();
        void Add(string key, double weight)
        {
            if (!features.TryGetValue(key, out var existing) || existing < weight)
            {
                features[key] = weight;
            }
        }

        foreach (var genre in title.Genres) Add("genre:" + genre.ToLowerInvariant(), 1.0);
        foreach (var tag in title.Tags) Add("tag:" + tag.ToLowerInvariant(), 1.2);
        foreach (var studio in title.Studios) Add("studio:" + studio.ToLowerInvariant(), 0.6);
        foreach (var person in title.People)
        {
            Add("person:" + person.Name.ToLowerInvariant(), person.Role == "Actor" ? 0.6 : 1.0);
        }

        if (title.Year is { } year)
        {
            Add("decade:" + year / 10, 0.5);
        }

        foreach (Match word in Word().Matches(title.Overview?.ToLowerInvariant() ?? ""))
        {
            if (!StopWords.Contains(word.Value))
            {
                Add("word:" + word.Value, 0.4);
            }
        }

        return features;
    }

    [GeneratedRegex("[a-z][a-z']{3,}")]
    private static partial Regex Word();
}
