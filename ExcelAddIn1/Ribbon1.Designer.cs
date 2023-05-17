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
            this.tab1 = this.Factory.CreateRibbonTab();
            this.QB = this.Factory.CreateRibbonGroup();
            this.QBUtility = this.Factory.CreateRibbonButton();
            this.tab1.SuspendLayout();
            this.QB.SuspendLayout();
            this.SuspendLayout();
            // 
            // tab1
            // 
            this.tab1.ControlId.ControlIdType = Microsoft.Office.Tools.Ribbon.RibbonControlIdType.Office;
            this.tab1.Groups.Add(this.QB);
            this.tab1.Label = "TabAddIns";
            this.tab1.Name = "tab1";
            // 
            // QB
            // 
            this.QB.Items.Add(this.QBUtility);
            this.QB.Label = "QB";
            this.QB.Name = "QB";
            // 
            // QBUtility
            // 
            this.QBUtility.Label = "QBUtility";
            this.QBUtility.Name = "QBUtility";
            this.QBUtility.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.button1_Click);
            // 
            // QBRibbon
            // 
            this.Name = "QBRibbon";
            this.RibbonType = "Microsoft.Excel.Workbook";
            this.Tabs.Add(this.tab1);
            this.Load += new Microsoft.Office.Tools.Ribbon.RibbonUIEventHandler(this.Ribbon1_Load);
            this.tab1.ResumeLayout(false);
            this.tab1.PerformLayout();
            this.QB.ResumeLayout(false);
            this.QB.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        internal Microsoft.Office.Tools.Ribbon.RibbonTab tab1;
        internal Microsoft.Office.Tools.Ribbon.RibbonGroup QB;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton QBUtility;
    }

    partial class ThisRibbonCollection
    {
        internal QBRibbon Ribbon1
        {
            get { return this.GetRibbon<QBRibbon>(); }
        }
    }
}
