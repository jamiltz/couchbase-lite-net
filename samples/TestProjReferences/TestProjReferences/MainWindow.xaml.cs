using Couchbase.Lite;
using Couchbase.Lite.Listener.Tcp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace TestProjReferences
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private Database database;
        private Query query;
        private View view;
        public MainWindow()
        {
            InitializeComponent();

            Manager manager = Manager.SharedInstance;
            manager.StorageType = "SQLite";
            CouchbaseLiteTcpListener listener = new CouchbaseLiteTcpListener(manager, 3689);
            listener.Start();
            this.database = manager.GetDatabase("db");

            view = database.GetView("myview");
            view.SetMap((doc, emit) =>
            {
                Console.WriteLine($"The id of the document is {doc["ids"]}");
                emit(doc["_id"], doc["time"]);
            }, "1");
            query = view.CreateQuery();

            Timer timer = new Timer();
            timer.Interval = 5000;
            timer.Elapsed += Timer_Elapsed;
            //timer.Start();

            database.Changed += Database_Changed;
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Random rnd = new Random();
            int month = rnd.Next(1, 13);

            Document document = database.GetExistingDocument("test");
            if (document != null)
            {
                var p = new Dictionary<string, object>()
                {
                    {"time", DateTime.Now.ToString() },
                    {"_rev", document.CurrentRevisionId }
                };
                document.PutProperties(p);
            } else
            {
                document = database.GetDocument("test");
                var p = new Dictionary<string, object>()
                {
                    {"time", DateTime.Now.ToString() }
                };
                document.PutProperties(p);
            }
        }

        private void Database_Changed(object sender, DatabaseChangeEventArgs e)
        {
            foreach (var change in e.Changes)
            {
                Console.WriteLine($"New document added {change.DocumentId} / {change.RevisionId}", change.DocumentId);
                Console.WriteLine("Running query...");
                Query query = view.CreateQuery();
                QueryEnumerator rows = query.Run();
                //foreach (var row in rows)
                //{
                //    Console.WriteLine($"Row: {row.DocumentRevisionId}");
                //}
            }
        }
    }
}
