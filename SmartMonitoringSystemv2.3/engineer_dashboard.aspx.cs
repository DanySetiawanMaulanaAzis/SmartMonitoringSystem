using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SmartMonitoringSystemv2._3
{
    public partial class engineer_dashboard : System.Web.UI.Page
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
                await Task.Run(() => LoadUserPerformance());
            }
        }



        //APACHE ECHARTS
        [System.Web.Services.WebMethod]
        public static object GetCompletedTasksData()
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;

            List<string> dates = new List<string>();
            List<int> completedTasks = new List<int>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string query = @"
                SELECT CAST(EndTime AS DATE) AS WorkDate, COUNT(*) AS TotalCompleted
                FROM WorkLogNew
                WHERE StatusId = 2 AND EndTime >= DATEADD(DAY, -7, GETDATE())
                GROUP BY CAST(EndTime AS DATE)
                ORDER BY WorkDate";

                SqlCommand cmd = new SqlCommand(query, conn);
                conn.Open();
                SqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    dates.Add(Convert.ToDateTime(reader["WorkDate"]).ToString("dd-MM-yyyy"));
                    completedTasks.Add(Convert.ToInt32(reader["TotalCompleted"]));
                }
                conn.Close();
            }

            // Debugging log untuk memastikan data
            System.Diagnostics.Debug.WriteLine("Dates: " + string.Join(",", dates));
            System.Diagnostics.Debug.WriteLine("Tasks: " + string.Join(",", completedTasks));

            return new { dates, completedTasks };
        }


        [System.Web.Services.WebMethod]
        public static object GetUserPerformanceData()
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;

            List<string> usernames = new List<string>();
            List<int> totalCompletedTasks = new List<int>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string query = @"
            SELECT 
                u.Username, 
                COUNT(w.WorkLogId) AS TotalCompletedTasks
            FROM 
                Users u
            LEFT JOIN 
                WorkLogNew w ON u.UserId = w.UserId AND w.StatusId = 2
            WHERE 
                u.is_employee = 1
            GROUP BY 
                u.Username
            ORDER BY 
                TotalCompletedTasks DESC";

                SqlCommand cmd = new SqlCommand(query, conn);
                conn.Open();
                SqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    usernames.Add(reader["Username"].ToString());
                    totalCompletedTasks.Add(Convert.ToInt32(reader["TotalCompletedTasks"]));
                }
                conn.Close();
            }

            return new { usernames, totalCompletedTasks };
        }
        //APACHE ECHARTS


        //Load Tabel
        private async Task LoadUserPerformance()
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;

            List<string> usernames = new List<string>();
            List<int> finishedToday = new List<int>();
            List<string> averageWorkmanship = new List<string>();
            List<int> totalWorkmanship = new List<int>();
            List<int> predictedTomorrow = new List<int>();
            List<string> performanceResult = new List<string>();
            


            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string query = @"
                SELECT u.Username, 
                    (SELECT COUNT(*) FROM WorkLogNew WHERE UserId = u.UserId AND CAST(StartTime AS DATE) = CAST(GETDATE() AS DATE) AND StatusId = 2) AS FinishedToday,
                    (SELECT ISNULL(AVG(TotalTime), 0) FROM WorkLogNew WHERE UserId = u.UserId AND StatusId = 2) AS AverageWorkmanship,
                    (SELECT COUNT(*) FROM WorkLogNew WHERE UserId = u.UserId AND StatusId = 2) AS TotalWorkmanship
                FROM Users u
                WHERE u.is_employee = 1";

                SqlCommand cmd = new SqlCommand(query, conn);
                conn.Open();
                SqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    usernames.Add(reader["Username"].ToString());
                    int finished = Convert.ToInt32(reader["FinishedToday"]);
                    finishedToday.Add(finished);

                    int avgWorkSeconds = Convert.ToInt32(reader["AverageWorkmanship"]);
                    TimeSpan avgTimeSpan = TimeSpan.FromSeconds(avgWorkSeconds);
                    string avgFormatted = string.Format("{0:D2}:{1:D2}:{2:D2}", avgTimeSpan.Hours, avgTimeSpan.Minutes, avgTimeSpan.Seconds);
                    averageWorkmanship.Add(avgFormatted);

                    int totalWork = Convert.ToInt32(reader["TotalWorkmanship"]);
                    totalWorkmanship.Add(totalWork);

                    int userId = GetUserIdByUsername(reader["Username"].ToString());


                    // Panggil API untuk prediksi Pekerjaan yang akan diselesaikan besok
                    List<float[]> sequence = await GetUserSequenceAsync(userId);

                    int predictionTomorrow = await GetLSTMPredictionAsync(sequence);
                    predictedTomorrow.Add(predictionTomorrow);



                    // Panggil API untuk prediksi performa karyawan
                    int prediction = await GetPerformancePredictionAsync(finished, avgWorkSeconds, totalWork);

                    string performanceLabel = "Tidak Diketahui";
                    switch (prediction)
                    {
                        case 0:
                            performanceLabel = "Rendah";
                            break;
                        case 1:
                            performanceLabel = "Sedang";
                            break;
                        case 2:
                            performanceLabel = "Tinggi";
                            break;
                    }


                    performanceResult.Add(performanceLabel);
                }

                conn.Close();
            }

            // Build table HTML
            StringBuilder tableBodyHtml = new StringBuilder();
            for (int i = 0; i < usernames.Count; i++)
            {
                string color = "#f0f0f0";
                switch (performanceResult[i])
                {
                    case "Rendah":
                        color = "#f8d7da";
                        break;
                    case "Sedang":
                        color = "#fff3cd";
                        break;
                    case "Tinggi":
                        color = "#d4edda";
                        break;
                }


                tableBodyHtml.Append("<tr>");
                tableBodyHtml.Append($"<td>{usernames[i]}</td>");
                tableBodyHtml.Append($"<td>{finishedToday[i]}</td>");
                tableBodyHtml.Append($"<td>{averageWorkmanship[i]}</td>");
                tableBodyHtml.Append($"<td>{totalWorkmanship[i]}</td>");
                tableBodyHtml.Append($"<td>{predictedTomorrow[i]}</td>");
                tableBodyHtml.Append($"<td style='background-color: {color};'>{performanceResult[i]}</td>");
                tableBodyHtml.Append("</tr>");
            }

            userTableBody.InnerHtml = tableBodyHtml.ToString();
        }



        private async Task<List<float[]>> GetUserSequenceAsync(int userId)
        {
            List<float[]> sequence = new List<float[]>();
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string query = @"
                WITH Last7Days AS (
                SELECT CAST(GETDATE() - number AS DATE) AS WorkDate
                FROM master..spt_values
                WHERE type = 'P' AND number < 7
                )
                SELECT 
                    d.WorkDate,
                    ISNULL(s.FinishedToday, 0) AS FinishedToday,
                    ISNULL(s.EfficiencyScore, 0) AS EfficiencyScore,
                    ISNULL(s.TotalWorkmanship, 0) AS TotalWorkmanship
                FROM Last7Days d
                LEFT JOIN DailyUserSummary s ON s.WorkDate = d.WorkDate AND s.UserId = @UserId
                ORDER BY d.WorkDate
                ";

                SqlCommand cmd = new SqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@UserId", userId);

                await conn.OpenAsync();
                SqlDataReader reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    int finished = Convert.ToInt32(reader["FinishedToday"]);
                    int efficiency = Convert.ToInt32(reader["EfficiencyScore"]);
                    int total = Convert.ToInt32(reader["TotalWorkmanship"]);

                    sequence.Add(new float[] { finished, efficiency, total });
                }
            }

            // Reverse agar urutan dari paling lama ke paling baru
            sequence.Reverse();
            return sequence;
        }


        private int GetUserIdByUsername(string username)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString;

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string query = "SELECT UserId FROM Users WHERE Username = @Username";
                SqlCommand cmd = new SqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@Username", username);
                conn.Open();

                object result = cmd.ExecuteScalar();
                if (result != null && int.TryParse(result.ToString(), out int userId))
                {
                    return userId;
                }
                else
                {
                    return -1; // User tidak ditemukan
                }
            }
        }



        //API
        private async Task<int> GetPerformancePredictionAsync(int finishedToday, int avgWorkInSeconds, int totalWorkmanship)
        {
            using (var client = new HttpClient())
            {
                var requestData = new
                {
                    FinishedToday = finishedToday,
                    AvgWorkmanship = avgWorkInSeconds,
                    TotalWorkmanship = totalWorkmanship
                };

                var json = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Ganti URL ini dengan URL aktual dari Flask API kamu
                string flaskUrl = "http://127.0.0.1:5000/predict/performance";

                var response = await client.PostAsync(flaskUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseContent);
                    return (int)result.PerformanceCategory;
                }
                else
                {
                    return -1; // gagal
                }
            }
        }


        private async Task<int> GetLSTMPredictionAsync(List<float[]> sequence)
        {
            using (var client = new HttpClient())
            {
                var payload = new { sequence = sequence };
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync("http://127.0.0.1:5000/predict/workload", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseString);
                    return (int)Math.Round((float)result.PredictedFinishedTomorrow);
                }

                return -1; // fallback
            }
        }
        //API
    }
}