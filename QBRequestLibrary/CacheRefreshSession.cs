using System;
using System.Diagnostics;

namespace QBRequestLibrary
{
    public interface ICacheRefreshSessionFactory
    {
        ICacheRefreshSession OpenCacheRefreshSession();
    }

    public interface ICacheRefreshSession : IDisposable
    {
        IAllItemNonInvQueryRequest Items { get; }
        ICommercialTermsQueryRequest Terms { get; }
        ICustomerCommercialTermsQueryRequest Customers { get; }
    }

    // One short-lived session for a full refresh. Never shared across threads or writes.
    public sealed class CacheRefreshSession : ICacheRefreshSession
    {
        private readonly Connection _connection;
        public IAllItemNonInvQueryRequest Items { get; }
        public ICommercialTermsQueryRequest Terms { get; }
        public ICustomerCommercialTermsQueryRequest Customers { get; }

        public CacheRefreshSession(Connection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            try
            {
                var timer = Stopwatch.StartNew();
                _connection.Open();
                new Logger().LogInfo($"QB cache session: connect_ms={timer.ElapsedMilliseconds}.");
                var items = new AllItemNonInvQueryRequest();
                var terms = new CommercialTermsQueryRequest();
                var customers = new CustomerCommercialTermsQueryRequest();
                items.UseOpenConnection(connection);
                terms.UseOpenConnection(connection);
                customers.UseOpenConnection(connection);
                Items = items;
                Terms = terms;
                Customers = customers;
            }
            catch
            {
                _connection.Dispose();
                throw;
            }
        }

        public void Dispose() => _connection.Dispose();
    }
}
