using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace QuickBooksIPCContracts
{
    [DataContract]
    public class QBItem
    {
        [DataMember]
        public string Number { get; set; }

        [DataMember]
        public string Description { get; set; }

        [DataMember]
        public double Rate { get; set; }

        [DataMember]
        public int Quantity { get; set; }

        [DataMember]
        public string AccountName { get; set; }

        [DataMember]
        public bool Active { get; set; }
    }

    [DataContract]
    public class QBOrder
    {
        [DataMember]
        public string QuoteNumber { get; set; }

        [DataMember]
        public QBCustomer Customer { get; set; }

        [DataMember]
        public DateTime DueDate { get; set; }

        [DataMember]
        public List<QBItem> Items { get; set; }
    }

    [DataContract]
    public enum QBQuoteTransactionType
    {
        [EnumMember]
        Estimate,

        [EnumMember]
        SalesOrder
    }

    [DataContract]
    public class QBQuoteUploadLine
    {
        [DataMember]
        public string Description { get; set; }

        [DataMember]
        public int Quantity { get; set; }

        [DataMember]
        public double Rate { get; set; }

        [DataMember]
        public string OverrideNumber { get; set; }
    }

    [DataContract]
    public class QBQuoteUploadRequest
    {
        [DataMember]
        public QBQuoteTransactionType TransactionType { get; set; }

        [DataMember]
        public string QuoteNumber { get; set; }

        [DataMember]
        public string CustomerAccountNumber { get; set; }

        [DataMember]
        public string CustomerName { get; set; }

        [DataMember]
        public QBCustomer Customer { get; set; }

        [DataMember]
        public string CustomerPO { get; set; }

        [DataMember]
        public DateTime DueDate { get; set; }

        [DataMember]
        public List<QBQuoteUploadLine> Lines { get; set; }
    }

    [DataContract]
    public class QBQuoteUploadResolvedLine
    {
        [DataMember]
        public string Number { get; set; }

        [DataMember]
        public string Description { get; set; }

        [DataMember]
        public int Quantity { get; set; }

        [DataMember]
        public double Rate { get; set; }

        [DataMember]
        public bool CreatedItem { get; set; }
    }

    [DataContract]
    public class QBQuoteUploadResult
    {
        [DataMember]
        public QBQuoteTransactionType TransactionType { get; set; }

        [DataMember]
        public string CustomerName { get; set; }

        [DataMember]
        public string QuoteNumber { get; set; }

        [DataMember]
        public List<QBQuoteUploadResolvedLine> Lines { get; set; }
    }

    [DataContract]
    public class QBCustomer
    {
        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public string AccountNumber { get; set; }

        [DataMember]
        public string PO { get; set; }
    }

    [DataContract]
    public class QBStatusResponse<T>
    {
        [DataMember]
        public string StatusMessage { get; set; }

        [DataMember]
        public int StatusCode { get; set; }
        
        [DataMember]
        public T Data { get; set; }
    }
}
