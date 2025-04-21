using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.UI.WebControls;

namespace SmartMonitoringSystemv2._3
{
    public partial class employee_dashboard : System.Web.UI.Page
    {
        protected async void Page_Load(object sender, EventArgs e)
        {
            // Cek jika session "userId" tidak ada, maka redirect ke login
            if (Session["userId"] == null)
            {
                Response.Redirect("login.aspx", false); // Perbaikan di sini
                Context.ApplicationInstance.CompleteRequest(); // Tambahkan ini
                return; // Pastikan tidak lanjut eksekusi
            }


            if (!IsPostBack)
            {
                LoadProducts();
                LoadWorkLogs();
                await LoadDashboardData(); // Tambahkan ini
                //await InsertDailySummary();
                await InsertOrUpdateDailySummary();

            }
        }


        private async Task LoadDashboardData()
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            int userId = Convert.ToInt32(Session["userId"]);

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync(); // Gunakan OpenAsync

                SqlCommand cmdFinishedToday = new SqlCommand(@"
                SELECT COUNT(*) FROM WorkLogNew 
                WHERE UserId = @UserId AND CAST(StartTime AS DATE) = CAST(GETDATE() AS DATE) AND StatusId = 2", conn);
                cmdFinishedToday.Parameters.AddWithValue("@UserId", userId);
                int finishedToday = (int)await cmdFinishedToday.ExecuteScalarAsync(); // Gunakan ExecuteScalarAsync

                SqlCommand cmdAverageWorkmanship = new SqlCommand(@"
                SELECT ISNULL(AVG(TotalTime), 0) 
                FROM WorkLogNew 
                WHERE UserId = @UserId AND StatusId = 2", conn);
                cmdAverageWorkmanship.Parameters.AddWithValue("@UserId", userId);
                int avgWorkmanshipSeconds = (int)await cmdAverageWorkmanship.ExecuteScalarAsync();

                SqlCommand cmdTotalWorkmanship = new SqlCommand(@"
                SELECT COUNT(*) FROM WorkLogNew 
                WHERE UserId = @UserId AND StatusId = 2", conn);
                cmdTotalWorkmanship.Parameters.AddWithValue("@UserId", userId);
                int totalWorkmanship = (int)await cmdTotalWorkmanship.ExecuteScalarAsync();

                // Panggil API Flask untuk prediksi Efficiency Score
                int efficiencyScore = 0; // Default

                if (finishedToday > 0)
                {
                    efficiencyScore = await GetEfficiencyScore(finishedToday, avgWorkmanshipSeconds, totalWorkmanship);
                }


                conn.Close();

                // Format waktu kerja rata-rata
                TimeSpan avgTimeSpan = TimeSpan.FromSeconds(avgWorkmanshipSeconds);
                string avgWorkmanshipFormatted = string.Format("{0:D2}:{1:D2}:{2:D2}",
                    avgTimeSpan.Hours, avgTimeSpan.Minutes, avgTimeSpan.Seconds);

                // Simpan ke ViewState
                ViewState["FinishedToday"] = finishedToday;
                ViewState["AverageWorkmanship"] = avgWorkmanshipFormatted;
                ViewState["TotalWorkmanship"] = totalWorkmanship;
                ViewState["EfficiencyScore"] = efficiencyScore;
            }
        }


        // simpan data bersih ke tabel DailyUserSummary
        private async Task InsertOrUpdateDailySummary()
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();

                string query = @"
            SELECT 
                w.UserId,
                CAST(w.StartTime AS DATE) AS WorkDate,
                COUNT(*) AS FinishedToday,
                (
                    SELECT ISNULL(AVG(CAST(w2.TotalTime AS INT)), 0)
                    FROM WorkLogNew w2
                    WHERE w2.UserId = w.UserId 
                      AND w2.StatusId = 2 
                      AND CAST(w2.StartTime AS DATE) = CAST(w.StartTime AS DATE)
                ) AS AvgWorkmanship,
                (
                    SELECT COUNT(*) 
                    FROM WorkLogNew 
                    WHERE UserId = w.UserId AND StatusId = 2
                ) AS TotalWorkmanship
            FROM WorkLogNew w
            WHERE w.StatusId = 2
            GROUP BY w.UserId, CAST(w.StartTime AS DATE)
            ORDER BY w.UserId, CAST(w.StartTime AS DATE)";

                SqlCommand cmd = new SqlCommand(query, conn);
                SqlDataReader reader = await cmd.ExecuteReaderAsync();

                var summaries = new List<(int UserId, DateTime WorkDate, int FinishedToday, int AvgWorkmanship, int TotalWorkmanship)>();

                while (await reader.ReadAsync())
                {
                    int userId = reader.GetInt32(0);
                    DateTime workDate = reader.GetDateTime(1);
                    int finishedToday = reader.GetInt32(2);
                    int avgWorkmanship = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    int totalWorkmanship = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

                    summaries.Add((userId, workDate, finishedToday, avgWorkmanship, totalWorkmanship));
                }

                reader.Close();

                foreach (var summary in summaries)
                {
                    int efficiencyScore = await GetEfficiencyScore(summary.FinishedToday, summary.AvgWorkmanship, summary.TotalWorkmanship);

                    // Cek apakah data untuk user dan tanggal ini sudah ada
                    SqlCommand checkCmd = new SqlCommand(@"
                SELECT COUNT(*) 
                FROM DailyUserSummary 
                WHERE UserId = @UserId AND WorkDate = @WorkDate", conn);
                    checkCmd.Parameters.AddWithValue("@UserId", summary.UserId);
                    checkCmd.Parameters.AddWithValue("@WorkDate", summary.WorkDate);
                    int count = (int)await checkCmd.ExecuteScalarAsync();

                    if (count > 0)
                    {
                        // Update seluruh kolom
                        SqlCommand updateCmd = new SqlCommand(@"
                    UPDATE DailyUserSummary 
                    SET 
                        FinishedToday = @FinishedToday,
                        AvgWorkmanship = @AvgWorkmanship,
                        TotalWorkmanship = @TotalWorkmanship,
                        EfficiencyScore = @EfficiencyScore
                    WHERE UserId = @UserId AND WorkDate = @WorkDate", conn);
                        updateCmd.Parameters.AddWithValue("@UserId", summary.UserId);
                        updateCmd.Parameters.AddWithValue("@WorkDate", summary.WorkDate);
                        updateCmd.Parameters.AddWithValue("@FinishedToday", summary.FinishedToday);
                        updateCmd.Parameters.AddWithValue("@AvgWorkmanship", summary.AvgWorkmanship);
                        updateCmd.Parameters.AddWithValue("@TotalWorkmanship", summary.TotalWorkmanship);
                        updateCmd.Parameters.AddWithValue("@EfficiencyScore", efficiencyScore);
                        await updateCmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        // Insert data baru
                        SqlCommand insertCmd = new SqlCommand(@"
                    INSERT INTO DailyUserSummary 
                    (UserId, WorkDate, FinishedToday, AvgWorkmanship, TotalWorkmanship, EfficiencyScore)
                    VALUES (@UserId, @WorkDate, @FinishedToday, @AvgWorkmanship, @TotalWorkmanship, @EfficiencyScore)", conn);
                        insertCmd.Parameters.AddWithValue("@UserId", summary.UserId);
                        insertCmd.Parameters.AddWithValue("@WorkDate", summary.WorkDate);
                        insertCmd.Parameters.AddWithValue("@FinishedToday", summary.FinishedToday);
                        insertCmd.Parameters.AddWithValue("@AvgWorkmanship", summary.AvgWorkmanship);
                        insertCmd.Parameters.AddWithValue("@TotalWorkmanship", summary.TotalWorkmanship);
                        insertCmd.Parameters.AddWithValue("@EfficiencyScore", efficiencyScore);
                        await insertCmd.ExecuteNonQueryAsync();
                    }
                }
            }
        }


        private void LoadProducts()
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                SqlDataAdapter da = new SqlDataAdapter("SELECT productId, productName FROM products", conn);
                DataTable dt = new DataTable();
                da.Fill(dt);

                ddlProduct.DataSource = dt;
                ddlProduct.DataTextField = "productName";
                ddlProduct.DataValueField = "productId";
                ddlProduct.DataBind();

                // Tambahkan item default setelah DataBind()
                ddlProduct.Items.Insert(0, new ListItem("Select Product", "0"));

                // Pastikan dropdown kembali ke item default
                ddlProduct.SelectedIndex = 0;
            }
        }



        // Ketika product dipilih, simpan ke tabel WorkLog
        protected void ddlProduct_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (ddlProduct.SelectedValue == "0")
            {
                // Tidak melakukan apa-apa jika item default dipilih
                return;
            }

            int productId = Convert.ToInt32(ddlProduct.SelectedValue);
            int userId = Convert.ToInt32(Session["userId"]); // Mengambil userId dari session
            DateTime startTime = DateTime.Now;
            int statusId = 1; // Status "On Progress"

            // Menghitung TotalTime sejak StartTime dalam detik
            int totalTimeInSeconds = 0; // Set initial TotalTime to 0 saat pekerjaan dimulai

            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string insertQuery = @"
            INSERT INTO WorkLogNew (ProductId, UserId, StartTime, StatusId, TotalTime) 
            VALUES (@ProductId, @UserId, @StartTime, @StatusId, @TotalTime)";

                SqlCommand cmd = new SqlCommand(insertQuery, conn);
                cmd.Parameters.AddWithValue("@ProductId", productId);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@StartTime", startTime);
                cmd.Parameters.AddWithValue("@StatusId", statusId);
                cmd.Parameters.AddWithValue("@TotalTime", totalTimeInSeconds);  // TotalTime diset ke 0 detik pada saat dimulai

                conn.Open();
                cmd.ExecuteNonQuery();
                conn.Close();
            }

            // Redirect ke halaman yang sama untuk menghindari pengulangan data
            Response.Redirect(Request.RawUrl);
        }


        //DataTables
        private void LoadWorkLogs()
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                int userId = Convert.ToInt32(Session["userId"]);

                // Mengambil data log pekerjaan dengan kolom TotalTime yang sudah dihitung sebelumnya
                SqlDataAdapter da = new SqlDataAdapter(@"
                SELECT w.WorkLogId, p.productName, w.StartTime, w.EndTime, 
                       w.TotalTime, s.statusName
                FROM WorkLogNew w
                INNER JOIN products p ON w.ProductId = p.productId
                INNER JOIN statuses s ON w.StatusId = s.statusId
                WHERE w.UserId = @UserId
                ORDER BY w.CreatedAt DESC", conn);

                da.SelectCommand.Parameters.AddWithValue("@UserId", userId);

                DataTable dt = new DataTable();
                da.Fill(dt);

                StringBuilder sb = new StringBuilder();
                int rowIndex = 1;

                foreach (DataRow row in dt.Rows)
                {
                    sb.Append("<tr data-worklog-id='" + row["WorkLogId"] + "' data-total-time='" + row["TotalTime"] + "' data-status='" + row["statusName"] + "'>");

                    sb.AppendFormat("<td style='text-align: center;'>{0}</td>", rowIndex);
                    sb.AppendFormat("<td>{0}</td>", row["productName"]);
                    sb.AppendFormat("<td>{0}</td>", Convert.ToDateTime(row["StartTime"]).ToString("dd-MM-yyyy HH:mm"));

                    // Menangani nilai NULL pada TotalTime dan memastikan hanya menampilkan jam:menit:detik jika ada
                    int totalSeconds = row.IsNull("TotalTime") ? 0 : Convert.ToInt32(row["TotalTime"]);
                    TimeSpan timeSpan = TimeSpan.FromSeconds(totalSeconds);
                    sb.AppendFormat("<td style='text-align: center;'>{0}</td>", timeSpan.ToString(@"hh\:mm\:ss"));

                    string statusName = row["statusName"].ToString();
                    string badgeClass = statusName == "On Progress" ? "badge badge-warning blink" : "badge badge-success";
                    sb.AppendFormat("<td style='text-align:center'><span class='{0}'>{1}</span></td>", badgeClass, statusName);

                    string disableButtons = statusName == "Completed" ? "disabled" : "";

                    sb.AppendFormat("<td style='text-align:center'>");
                    sb.AppendFormat("<button type='button' class='btn btn-primary' onclick='markAsCompleted({0})' {1}>Done</button> ", row["WorkLogId"], disableButtons);
                    sb.AppendFormat("<button type='button' class='btn btn-danger' onclick='deleteWorkLog({0})' {1}>Delete</button>", row["WorkLogId"], disableButtons);
                    sb.AppendFormat("</td>");

                    sb.Append("</tr>");
                    rowIndex++;
                }

                workLogTBody.InnerHtml = sb.ToString();
            }
        }
        //DataTables



        [System.Web.Services.WebMethod]
        public static void UpdateTotalTime(int workLogId, int totalTime)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string updateQuery = @"
            UPDATE WorkLogNew
            SET TotalTime = @TotalTime
            WHERE WorkLogId = @WorkLogId";

                SqlCommand updateCmd = new SqlCommand(updateQuery, conn);
                updateCmd.Parameters.AddWithValue("@TotalTime", totalTime);
                updateCmd.Parameters.AddWithValue("@WorkLogId", workLogId);

                conn.Open();
                updateCmd.ExecuteNonQuery();
                conn.Close();
            }
        }



        [System.Web.Services.WebMethod]
        public static void MarkWorkLogAsCompleted(int workLogId)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                // Ambil StartTime dari WorkLog berdasarkan WorkLogId
                string selectQuery = "SELECT StartTime FROM WorkLogNew WHERE WorkLogId = @WorkLogId";
                SqlCommand selectCmd = new SqlCommand(selectQuery, conn);
                selectCmd.Parameters.AddWithValue("@WorkLogId", workLogId);

                conn.Open();
                SqlDataReader reader = selectCmd.ExecuteReader();

                DateTime startTime = DateTime.MinValue;

                if (reader.HasRows)
                {
                    reader.Read();
                    startTime = reader.GetDateTime(0); // Ambil StartTime yang sudah ada
                }
                reader.Close();

                // Update EndTime menjadi waktu sekarang dan set StatusId menjadi 2 (Completed)
                string updateQuery = @"
                UPDATE WorkLogNew
                SET EndTime = @EndTime, 
                    StatusId = 2 -- Completed
                WHERE WorkLogId = @WorkLogId";

                SqlCommand updateCmd = new SqlCommand(updateQuery, conn);
                DateTime now = DateTime.Now; // Waktu sekarang
                updateCmd.Parameters.AddWithValue("@EndTime", now);
                updateCmd.Parameters.AddWithValue("@WorkLogId", workLogId);

                updateCmd.ExecuteNonQuery();
                conn.Close();
            }
        }


        [System.Web.Services.WebMethod]
        public static void DeleteWorkLog(int workLogId)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string deleteQuery = "DELETE FROM WorkLogNew WHERE WorkLogId = @WorkLogId";

                SqlCommand cmd = new SqlCommand(deleteQuery, conn);
                cmd.Parameters.AddWithValue("@WorkLogId", workLogId);

                conn.Open();
                cmd.ExecuteNonQuery();
                conn.Close();
            }
        }


        //APACHE ECHARTS
        [System.Web.Services.WebMethod]
        public static object GetWorkLogDataForChart(string startDate, string endDate)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;
            List<string> labels = new List<string>();
            List<int> data = new List<int>();
            int userId = Convert.ToInt32(HttpContext.Current.Session["userId"]); // Ambil userId dari session

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string query = @"
                SELECT 
                    CAST(StartTime AS DATE) AS WorkDate, 
                    COUNT(*) AS TotalWork
                FROM WorkLogNew
                WHERE UserId = @UserId AND StartTime BETWEEN @StartDate AND @EndDate
                GROUP BY CAST(StartTime AS DATE)
                ORDER BY WorkDate";

                SqlCommand cmd = new SqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@StartDate", startDate);
                cmd.Parameters.AddWithValue("@EndDate", endDate);
                cmd.Parameters.AddWithValue("@UserId", userId);  // Filter berdasarkan userId

                conn.Open();
                SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    labels.Add(Convert.ToDateTime(reader["WorkDate"]).ToString("dd-MM-yyyy"));
                    data.Add(Convert.ToInt32(reader["TotalWork"]));
                }
                conn.Close();
            }

            return new { labels, data };
        }


        //API
        public async Task<int> GetEfficiencyScore(int finishedToday, int avgWorkmanship, int totalWorkmanship)
        {
            using (HttpClient client = new HttpClient())
            {
                client.BaseAddress = new Uri("http://127.0.0.1:5000/"); // Ganti dengan IP server jika tidak lokal
                var requestData = new
                {
                    FinishedToday = finishedToday,
                    AvgWorkmanship = avgWorkmanship,
                    TotalWorkmanship = totalWorkmanship
                };

                string json = JsonConvert.SerializeObject(requestData);
                StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.PostAsync("predict/efficiency", content);
                if (response.IsSuccessStatusCode)
                {
                    string responseJson = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseJson);
                    return (int)result.EfficiencyScore;
                }
                return 0; // Default jika gagal
            }
        }
    }
}