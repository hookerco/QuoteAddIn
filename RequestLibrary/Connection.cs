using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Interop.QBFC14;

namespace RequestLibrary
{
	public class Connection
	{
		private bool sessionBegun = false;
		private bool connectionOpen = false;
		public QBSessionManager sessionManager
		{
			get { return internalManager; }
		}

		private QBSessionManager internalManager;

        /**
		 * <summary>Property <c>RequestType</c> is the current type of Request </summary>
		 * <remarks>Options are from <see cref="RequestLibrary.RequestType"/></remarks>
		 */
        RequestType RequestType { get; set; }

		/**
		 * <summary>Property <c>File</c> is the current QuickBooks company file</summary>
		 */
		public string File { get; set; } = string.Empty;

		/**
		 * <summary>This method opens the connection and begins the session.</summary>
		 */
		public bool Open()
		{
			try
			{
				internalManager = new QBSessionManager();

				//Create the message set request object to hold our request
				IMsgSetRequest requestMsgSet = internalManager.CreateMsgSetRequest("US", 14, 0);
				requestMsgSet.Attributes.OnError = ENRqOnError.roeContinue;

				internalManager.OpenConnection("", "Sample Code from OSR");
				connectionOpen = true;
				internalManager.BeginSession(File, ENOpenMode.omDontCare);
				sessionBegun = true;

				return true;
			}

			catch (Exception e)
			{ // Cleanly close session and connection 
				MessageBox.Show(e.Message);
				if (sessionBegun)
				{
					internalManager.EndSession();
				}
				if (connectionOpen)
				{
					internalManager.CloseConnection();
				}

				return false;
			}
		}

        /**
		 * <summary>This method ends session and closes connection if applicable.</summary>
		 */
        public void Close()
		{
            if (sessionBegun)
            {
                sessionManager.EndSession();
            }
            if (connectionOpen)
            {
                sessionManager.CloseConnection();
            }
        }

		/**
		 * <summary>Destructor. Calls <see cref="Close"/>.</summary>
		 */
		~Connection()
		{
			Close();
		}
	}

    /**
	 * <summary>Enum <c>Type</c> contains CustomerQuery,
		EstimateAdd,
		ItemQuery,
		ItemAdd</summary>
	 */
    enum RequestType
	{
		CustomerQuery,
		EstimateAdd,
		ItemQuery,
		ItemAdd
	}
}
