using Interop.QBFC14;
using Moq;
using NUnit.Framework;
using QBRequestLibrary;
using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace QuickBooksServiceLibrary.Tests
{
    [TestFixture]
    public class QBRequestTests
    {
        [Test]
        public void AllItemNonInvQueryRequest_WhenServiceQueryFails_ReturnsFailureStatus()
        {
            var request = CreateRequestWithoutConnectingOrLogging();
            var responseSet = CreateResponseSet(
                CreateResponse(ENResponseType.rtItemNonInventoryQueryRs, 0, "Status OK"),
                CreateResponse(ENResponseType.rtItemServiceQueryRs, 3250, "Service query failed"));

            var result = request.Convert(responseSet);

            Assert.AreEqual(3250, result.StatusCode);
            Assert.AreEqual("Service query failed", result.StatusMessage);
        }

        private static TestableAllItemNonInvQueryRequest CreateRequestWithoutConnectingOrLogging()
        {
            var request = (TestableAllItemNonInvQueryRequest)FormatterServices.GetUninitializedObject(
                typeof(TestableAllItemNonInvQueryRequest));
            GC.SuppressFinalize(request);
            return request;
        }

        private static IMsgSetResponse CreateResponseSet(params IResponse[] responses)
        {
            var responseList = new Mock<IResponseList>();
            responseList.SetupGet(l => l.Count).Returns(responses.Length);
            responseList.Setup(l => l.GetAt(It.IsAny<int>())).Returns((int i) => responses[i]);

            var responseSet = new Mock<IMsgSetResponse>();
            responseSet.SetupGet(r => r.ResponseList).Returns(responseList.Object);
            return responseSet.Object;
        }

        private static IResponse CreateResponse(ENResponseType responseType, int statusCode, string statusMessage)
        {
            var type = new Mock<IResponseType>();
            type.Setup(t => t.GetValue()).Returns((short)responseType);

            var response = new Mock<IResponse>();
            response.SetupGet(r => r.Type).Returns(type.Object);
            response.SetupGet(r => r.StatusCode).Returns(statusCode);
            response.SetupGet(r => r.StatusMessage).Returns(statusMessage);
            response.SetupGet(r => r.Detail).Returns((IQBBase)null);
            return response.Object;
        }

        private sealed class TestableAllItemNonInvQueryRequest : AllItemNonInvQueryRequest
        {
            public QBStatusResponse<List<QBItem>> Convert(IMsgSetResponse responseSet)
            {
                return ConvertResponse(responseSet);
            }
        }
    }
}
