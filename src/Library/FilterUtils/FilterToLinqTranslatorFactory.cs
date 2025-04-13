using System.Collections.Concurrent;

namespace Library.FilterUtils;

public static class FilterToLinqTranslatorFactory
{
    private static readonly ConcurrentDictionary<Type, IFilterToLinqTranslator> TranslatorCache = new();

    public static FilterToLinqToLinqTranslator<TDoc> Create<TDoc>() =>
        (FilterToLinqToLinqTranslator<TDoc>)Create(typeof(TDoc));

    public static IFilterToLinqTranslator Create(Type docType) =>
        TranslatorCache.GetOrAdd(docType, CreateTranslatorForType);

    private static IFilterToLinqTranslator CreateTranslatorForType(Type docType)
    {
        var translatorType = typeof(FilterToLinqToLinqTranslator<>).MakeGenericType(docType);
        var factory = TranslatorFactoryCache.GetFactory(translatorType);

        return factory();
    }

    // why? we don't want to create dynamic code for "new" each time,
    // and it's faster than doing Activator.CreateInstance
    private static class TranslatorFactoryCache
    {
        private static readonly ConcurrentDictionary<Type, Func<IFilterToLinqTranslator>> FactoryCache = new();

        public static Func<IFilterToLinqTranslator> GetFactory(Type translatorType)
        {
            return FactoryCache.GetOrAdd(translatorType, type =>
            {
                var ctor = type.GetConstructor(Type.EmptyTypes)
                           ?? throw new InvalidOperationException($"Type {type} must have a parameterless constructor");

                var lambda = Expression.Lambda<Func<IFilterToLinqTranslator>>(Expression.New(ctor));
                return lambda.Compile();
            });
        }
    }
}