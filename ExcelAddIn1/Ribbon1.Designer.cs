namespace ExcelAddIn1
{
    partial class QBRibbon : Microsoft.Office.Tools.Ribbon.RibbonBase
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        public QBRibbon()
            : base(Globals.Factory.GetRibbonFactory())
        {
            InitializeComponent();
        }

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(QBRibbon));
            this.QBTab = this.Factory.CreateRibbonTab();
            this.QBGroup = this.Factory.CreateRibbonGroup();
            this.SendButton = this.Factory.CreateRibbonButton();
            this.QBTab.SuspendLayout();
            this.QBGroup.SuspendLayout();
            this.SuspendLayout();
            // 
            // QBTab
            // 
            this.QBTab.ControlId.ControlIdType = Microsoft.Office.Tools.Ribbon.RibbonControlIdType.Office;
            this.QBTab.Groups.Add(this.QBGroup);
            this.QBTab.Label = "Send to QuickBooks";
            this.QBTab.Name = "QBTab";
            // 
            // QBGroup
            // 
            this.QBGroup.Items.Add(this.SendButton);
            this.QBGroup.Name = "QBGroup";
            // 
            // SendButton
            // 
            this.SendButton.ControlSize = Microsoft.Office.Core.RibbonControlSize.RibbonControlSizeLarge;
            this.SendButton.Image = ((System.Drawing.Image)(resources.GetObject("SendButton.Image")));
            this.SendButton.Label = "Send";
            this.SendButton.Name = "SendButton";
            this.SendButton.ShowImage = true;
            this.SendButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.button1_Click);
            // 
            // QBRibbon
            // 
            this.Name = "QBRibbon";
            this.RibbonType = "Microsoft.Excel.Workbook";
            this.Tabs.Add(this.QBTab);
            this.Load += new Microsoft.Office.Tools.Ribbon.RibbonUIEventHandler(this.Ribbon1_Load);
            this.QBTab.ResumeLayout(false);
            this.QBTab.PerformLayout();
            this.QBGroup.ResumeLayout(false);
            this.QBGroup.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        internal Microsoft.Office.Tools.Ribbon.RibbonTab QBTab;
        internal Microsoft.Office.Tools.Ribbon.RibbonGroup QBGroup;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton SendButton;
    }

    partial class ThisRibbonCollection
    {
        internal QBRibbon Ribbon1
        {
            get { return this.GetRibbon<QBRibbon>(); }
        }
    }
}
