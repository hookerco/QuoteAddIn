using System;
using Interop.QBFC14;

namespace QBRequestLibrary
{
    /**
	 * <summary>Class <c>Connection</c> is a simplification of a QuickBooks connection and session</summary>
	 * <remarks>***May add request and response abstractions in future***</remarks>
	 */
    public class Connection
    {
        private bool sessionBegun = false;
        private bool connectionOpen = false;
        private QBSessionManager internalManager;
        private ENOpenMode userMode = ENOpenMode.omDontCare;

        /**
		 * <summary>Property <c>sessionManager</c> is a read-only instance
		 * of a QBSessionManager</summary>
		 */
        public QBSessionManager SessionManager
        {
            get { return internalManager; }
        }

        /**
		 * <summary>Method <c>setUserMode</c> sets userMode </summary>
		 * <remarks> Not caps sensitive, "single" = Single User Mode, "multi" = Multi User Mode </remarks>
		 */
        public void SetUserMode(string mode)
        {
            mode = mode.ToLower();
            if (mode == "single" || mode == "s")
            {
                userMode = ENOpenMode.omSingleUser;
            }
            else if (mode == "multi" || mode == "m")
            {
                userMode = ENOpenMode.omMultiUser;
            }
            else
            {
                userMode = ENOpenMode.omDontCare;
            }
        }

        /**
		 * <summary>Property <c>File</c> is the current QuickBooks company file</summary>
		 */
        public string File { get; set; } = string.Empty;

        /**
		 * <summary>This method opens the connection and begins the session.</summary>
		 */
        public bool Open()
        {
            internalManager = new QBSessionManager();

            try
            {
                internalManager.OpenConnection("", "Proto-CAM QB Library");
                connectionOpen = true;
                internalManager.BeginSession(File, userMode);
                sessionBegun = true;

                return true;
            }

            catch (Exception)
            { // Cleanly close session and connection 
                SafeClose();
                throw new ConnectionException();
            }
        }

        /**
		 * <summary>This method ends session and closes connection if applicable.</summary>
		 */
        public void Close()
        {
            internalManager.EndSession();
            internalManager.CloseConnection();
        }

        private void SafeClose()
        {
            if (sessionBegun)
            {
                internalManager.EndSession();
                sessionBegun = false;
            }
            if (connectionOpen)
            {
                internalManager.CloseConnection();
                connectionOpen = false;
            }
        }

        /**
		 * <summary>Destructor. Calls <see cref="Close"/>.</summary>
		 */
        ~Connection()
        {
            Close();
        }

        public class ConnectionException : Exception
        {
            public ConnectionException(string message = "Could not begin session") : base(message) { }
        }
    }
}
