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
