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
        /// <param name="disposing">true if managed resources should be
        ///     disposed; otherwise, false.</param>
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
        /// Required method for Designer support - do not modify the contents of
        /// this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(QBRibbon));
            this.TabAddIns = this.Factory.CreateRibbonTab();
            this.OpenFileDialog1 = new System.Windows.Forms.OpenFileDialog();
            this.QuoteOpenFileDialog = new System.Windows.Forms.OpenFileDialog();
            this.QBTab = this.Factory.CreateRibbonTab();
            this.QuoteBuilderGroup = this.Factory.CreateRibbonGroup();
            this.CreateButton = this.Factory.CreateRibbonButton();
            this.AddButton = this.Factory.CreateRibbonButton();
            this.QBGroup = this.Factory.CreateRibbonGroup();
            this.SalesOrderButton = this.Factory.CreateRibbonButton();
            this.TabAddIns.SuspendLayout();
            this.QBTab.SuspendLayout();
            this.QuoteBuilderGroup.SuspendLayout();
            this.QBGroup.SuspendLayout();
            this.SuspendLayout();
            // 
            // TabAddIns
            // 
            this.TabAddIns.ControlId.ControlIdType = Microsoft.Office.Tools.Ribbon.RibbonControlIdType.Office;
            this.TabAddIns.Label = "TabAddIns";
            this.TabAddIns.Name = "TabAddIns";
            // 
            // QuoteOpenFileDialog
            // 
            this.QuoteOpenFileDialog.DefaultExt = "xlsx";
            // 
            // QBTab
            // 
            this.QBTab.Groups.Add(this.QuoteBuilderGroup);
            this.QBTab.Groups.Add(this.QBGroup);
            this.QBTab.Label = "QBUtility";
            this.QBTab.Name = "QBTab";
            // 
            // QuoteBuilderGroup
            // 
            this.QuoteBuilderGroup.Items.Add(this.CreateButton);
            this.QuoteBuilderGroup.Items.Add(this.AddButton);
            this.QuoteBuilderGroup.Label = "Quote Builder";
            this.QuoteBuilderGroup.Name = "QuoteBuilderGroup";
            // 
            // CreateButton
            // 
            this.CreateButton.Label = "Create";
            this.CreateButton.Name = "CreateButton";
            this.CreateButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.CreateButton_Click);
            // 
            // AddButton
            // 
            this.AddButton.Label = "Add";
            this.AddButton.Name = "AddButton";
            this.AddButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.AddButton_Click);
            // 
            // QBGroup
            // 
            this.QBGroup.Items.Add(this.SalesOrderButton);
            this.QBGroup.Name = "QBGroup";
            // 
            // SalesOrderButton
            // 
            this.SalesOrderButton.ControlSize = Microsoft.Office.Core.RibbonControlSize.RibbonControlSizeLarge;
            this.SalesOrderButton.Image = ((System.Drawing.Image)(resources.GetObject("SalesOrderButton.Image")));
            this.SalesOrderButton.Label = "Prepare for Sales Order";
            this.SalesOrderButton.Name = "SalesOrderButton";
            this.SalesOrderButton.ShowImage = true;
            this.SalesOrderButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.SalesOrderButton_Click);
            // 
            // QBRibbon
            // 
            this.Name = "QBRibbon";
            this.RibbonType = "Microsoft.Excel.Workbook";
            this.Tabs.Add(this.TabAddIns);
            this.Tabs.Add(this.QBTab);
            this.Load += new Microsoft.Office.Tools.Ribbon.RibbonUIEventHandler(this.Ribbon1_Load);
            this.TabAddIns.ResumeLayout(false);
            this.TabAddIns.PerformLayout();
            this.QBTab.ResumeLayout(false);
            this.QBTab.PerformLayout();
            this.QuoteBuilderGroup.ResumeLayout(false);
            this.QuoteBuilderGroup.PerformLayout();
            this.QBGroup.ResumeLayout(false);
            this.QBGroup.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        internal Microsoft.Office.Tools.Ribbon.RibbonTab TabAddIns;
        private System.Windows.Forms.OpenFileDialog OpenFileDialog1;
        private System.Windows.Forms.OpenFileDialog QuoteOpenFileDialog;
        private Microsoft.Office.Tools.Ribbon.RibbonTab QBTab;
        internal Microsoft.Office.Tools.Ribbon.RibbonGroup QBGroup;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton SalesOrderButton;
        internal Microsoft.Office.Tools.Ribbon.RibbonGroup QuoteBuilderGroup;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton CreateButton;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton AddButton;
    }

    partial class ThisRibbonCollection
    {
        internal QBRibbon Ribbon1
        {
            get { return this.GetRibbon<QBRibbon>(); }
        }
    }
}
