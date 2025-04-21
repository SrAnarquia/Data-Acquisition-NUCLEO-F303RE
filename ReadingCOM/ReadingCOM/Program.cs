using System;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace devicesCOM
{
    class ProgramPort
    {
        //This two from below are the reconnection TimeSpan to evaluete when to try again and when it happened
        static DateTime? sqlFailureTime = null;
        static TimeSpan retryInterval = TimeSpan.FromSeconds(20); //Time delay this starts once the awaits code is reached

        static int acquisitionLimit;
        static int currentLogId = -1;

        //Time out of 3 to avoid waiting too much for status on CASE 1
        static string connectionString = "Server=serverName;Database=yourDatabase;Trusted_Connection=True;Connect Timeout=3;";
        private static Stopwatch acquisitionTimer = new Stopwatch();
        static SerialPort sP;

        //This is to identify when SQL Server is not Online
        static bool sqlServerAvailable = true;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Please give the current COM (e.g. COM3): ");
            string portName = Console.ReadLine();

            Console.WriteLine("Please type acquisition time (ms): ");
            acquisitionLimit = int.Parse(Console.ReadLine());

            sP = new SerialPort
            {
                PortName = portName,
                BaudRate = 9600,
                Parity = Parity.None,
                StopBits = StopBits.One,
                DataBits = 8,
                Handshake = Handshake.None
            };



            if (!portName.Contains("COM"))
            {
                Console.WriteLine("Invalid COM port.");
                return;
            }

            try
            {
                Console.WriteLine("Please wait for 3 seconds while we reach SQL database...");
                // Insert de log
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    string insertLogQuery = "INSERT INTO dataAcquisitionLogs (DATE_TIME_INIT) OUTPUT INSERTED.ID VALUES (@startTime)";
                    SqlCommand cmd = new SqlCommand(insertLogQuery, conn);
                    cmd.Parameters.AddWithValue("@startTime", DateTime.Now);
                    conn.Open();
                    currentLogId = (int)cmd.ExecuteScalar();
                    Console.WriteLine($"Log session created with ID: {currentLogId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SQL Offline: {ex.Message}");
                sqlServerAvailable = false;
                SaveToCsvFallback(-1, 0, -1, "Error on SQL Server connection");
            }

            // Open the SerialPort even on SQL Server fail
            OpenSerialPortOnFail();



            await Task.Delay(acquisitionLimit); // Time Limit for acquisitionCOM

            // Once the task of acquisitionLimit is done, then thest if SQL Server is available and then write END_TIME with ID of current Session
            if (sP.IsOpen)
            {
                sP.Close();
                acquisitionTimer.Stop();

                if (sqlServerAvailable)
                {
                    try
                    {
                        using (SqlConnection conn = new SqlConnection(connectionString))
                        {
                            string updateQuery = "UPDATE dataAcquisitionLogs SET DATE_TIME_FINISHED = @endTime WHERE ID = @logId";
                            SqlCommand cmd = new SqlCommand(updateQuery, conn);
                            cmd.Parameters.AddWithValue("@endTime", DateTime.Now);
                            cmd.Parameters.AddWithValue("@logId", currentLogId);
                            conn.Open();
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Log session updated with finish time.");
                        }
                    }
                    catch (Exception updateEx)
                    {
                        Console.WriteLine($"[!] Could not update log end time: {updateEx.Message}");
                        SaveToCsvFallback(-1, 0, currentLogId, "Error updating log end time");
                    }
                }

                Console.WriteLine($"Acquisition complete after {acquisitionLimit} ms. Connection closed.");
            }


        }


        //This method checks on which values are being sent from MCU to COM ports and write either on SQL SERVER DB or CSV buffer
        private static void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {

                string data = sP.ReadLine().Trim();
                Console.WriteLine($"Received data: [{data}]");

                // Processing data: "ADC: 2050, Voltage: 2.05"
                string[] parts = data.Split(',');

                string adcRaw = parts[0].Replace("ADC:", "").Trim();
                string voltageRaw = parts[1].Replace("Voltage:", "").Trim();

                int adcValue = int.Parse(adcRaw);
                decimal voltageValue = decimal.Parse(voltageRaw);
                int elapsedTime = (int)acquisitionTimer.ElapsedMilliseconds;

                if (sqlServerAvailable)
                {
                    try
                    {
                        using (SqlConnection conn = new SqlConnection(connectionString))
                        {
                            string query = "INSERT INTO dataAcquisitionSTM32 (DATA_VALUE, TIME_COUNT, ID_LOGS) VALUES(@data, @time, @logId)";
                            SqlCommand cmd = new SqlCommand(query, conn);
                            cmd.Parameters.AddWithValue("@data", voltageValue);
                            cmd.Parameters.AddWithValue("@time", elapsedTime);
                            cmd.Parameters.AddWithValue("@logId", currentLogId);

                            conn.Open();
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Data saved correctly to SQL.");
                        }
                    }
                    catch (Exception sqlEx)
                    {
                        Console.WriteLine($"[!] SQL error during data insert: {sqlEx.Message}");
                        //This message is shown when initialy the DB was online and for some reason it went offline so data will be on buffer with a SQL INSERT FAILED
                        SaveToCsvFallback(voltageValue, elapsedTime, currentLogId, "Falback mode ON");

                        if (sqlServerAvailable)
                        {
                            sqlServerAvailable = false;
                            sqlFailureTime = DateTime.Now;

                            Console.WriteLine("[!] SQL Server just went offline during insert. Triggering reconnection...");
                            StartSqlReconnectionTimer();
                        }

                    }
                }
                else
                {
                    SaveToCsvFallback(voltageValue, elapsedTime, currentLogId, "Fallback mode");

                    if (sqlFailureTime == null)
                    {
                        sqlFailureTime = DateTime.Now;

                        // Throws the reconnection on second plane with a thread 
                        StartSqlReconnectionTimer();
                    }

                    sqlServerAvailable = false;




                }


            }
            catch (Exception ex)
            {
                Console.WriteLine($"[X] General error in DataReceived: {ex.Message}");
                SaveToCsvFallback(-1, 0, currentLogId, "Malformed or unreadable data");
            }
        }




        //We call this method each time we want to save data on csv buffer
        static void SaveToCsvFallback(decimal voltage, int time, int logId, string note = "")
        {
            try
            {
                string filePath = $"dataBuffer_{DateTime.Now:yyyyMMdd}.csv";

                //if file from this moment does not exists then we create it
                if (!File.Exists(filePath))
                {
                    string header = "DATA_VALUE,TIME_COUNT,ID_LOGS,NOTE";
                    File.AppendAllText(filePath, header + Environment.NewLine);
                }

                //We create the string line of data acquisition
                string line = $"{voltage},{time},{logId},{note}";

                //Now we append it to the CSV buffer
                File.AppendAllText(filePath, line + Environment.NewLine);

                Console.WriteLine("[*] Data was correctly saved on CSV");



            }
            catch (Exception exFile)
            {
                //If Fallback fails then this messsage is shown
                Console.WriteLine($"[X] Error data was not saved on CSV:{exFile.Message}");
            }

        }



        //this two methos from below are used to reconnect SQL DATABASE and checking status on server
        static void StartSqlReconnectionTimer()
        {
            Task.Run(async () =>
            {
                Console.WriteLine("[RECONNECT] SQL reconnection thread started");

                while (!sqlServerAvailable)
                {
                    await Task.Delay(retryInterval); // wait before trying to reconnect (20 seconds)

                    Console.WriteLine("[RECONNECT] Trying to reconnect to SQL Server");

                    if (TryReconnect()) //This evaluates if SQL Sever either is ON or is not, and if thats ON then choose a case
                    {
                        Console.WriteLine("[RECONNECT] SQL Server is back online");

                        // CASE 1: If we already had a LOG then we just wait 20 seconds more to keep using SQL
                        if (currentLogId != -1)
                        {
                            Console.WriteLine("[RECONNECT] Waiting 20s before resuming SQL inserts...");
                            await Task.Delay(TimeSpan.FromSeconds(20));
                            sqlServerAvailable = true;
                            sqlFailureTime = null;
                            Console.WriteLine("[RECONNECT] SQL writes resumed after wait.");
                        }
                        else
                        {
                            // CASE 2: Database was not online by the beggining
                            try
                            {
                                using (SqlConnection conn = new SqlConnection(connectionString))
                                {
                                    string insertLogQuery = "INSERT INTO dataAcquisitionLogs (DATE_TIME_INIT) OUTPUT INSERTED.ID VALUES (@startTime)";
                                    SqlCommand cmd = new SqlCommand(insertLogQuery, conn);
                                    cmd.Parameters.AddWithValue("@startTime", DateTime.Now);
                                    conn.Open();
                                    currentLogId = (int)cmd.ExecuteScalar();
                                    Console.WriteLine($"[RECONNECT] New log session created with ID: {currentLogId}");
                                    Console.WriteLine("[RECONNECT] Waiting 20s before resuming SQL inserts...");
                                    //This console line will show me how much offline time it happened before going to online
                                    //Console.WriteLine($"[RECONNECT] Total offline time: {sqlFailureTime.ToString()}");
                                    await Task.Delay(TimeSpan.FromSeconds(20));
                                    sqlServerAvailable = true;
                                    sqlFailureTime = null;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[RECONNECT] Failed to create new log session: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("[RECONNECT] SQL Server still offline.");
                    }
                }
            });
        }



        //This a method to reconnect database
        private static bool TryReconnect()
        {
            try
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    // Test to evaluate connection without creating problems on DB
                    using (SqlCommand cmd = new SqlCommand("SELECT 1", conn))
                    {
                        var result = cmd.ExecuteScalar();
                        return result != null;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        static void OpenSerialPortOnFail()
        {
            try
            {
                sP.Open();
                Console.WriteLine("Connection opened. Waiting for data...");
                acquisitionTimer.Start();
                sP.DiscardInBuffer();
                sP.DataReceived += new SerialDataReceivedEventHandler(SerialPort_DataReceived);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Could not open port: {ex.Message}");
            }
        }







    }
}
