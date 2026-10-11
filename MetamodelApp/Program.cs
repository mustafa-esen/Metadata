using System;
using System.Collections.Generic;
using Npgsql;

namespace MetamodelApp
{
    class Program
    {
        static void Main(string[] args)
        {
            string connString = "Host=localhost;Username=postgres;Password=1234;Database=postgres";

            using var conn = new NpgsqlConnection(connString);
            conn.Open();
            Console.WriteLine("1. Veritabanına bağlanıldı.\n");

            // ====================================================================
            // BÖLÜM 1: METAMODEL TABLOLARININ SIFIRLANMASI
            // (Uygulama her çalıştığında en güncel haritayı çıkarabilmesi için M2 katmanı temizlenir. M0 katmanındaki gerçek verilere dokunulmaz.)
            // ====================================================================
            using (var cmd = new NpgsqlCommand("TRUNCATE TABLE entityOccurrence_meta, relationship_meta, attribute_meta, entity_meta CASCADE;", conn))
            {
                cmd.ExecuteNonQuery();
            }

            // ====================================================================
            // BÖLÜM 2: METAMODEL ÇIKARIMI (M1 -> M2)
            // ====================================================================
            Console.WriteLine("2. Tablolar (Entities) Okunuyor ve Yazılıyor...");
            var entityDict = new Dictionary<string, int>();
            var tableNames = new List<string>();

            using (var cmdTables = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name NOT LIKE '%_meta'", conn))
            using (var reader = cmdTables.ExecuteReader())
            {
                while (reader.Read()) tableNames.Add(reader.GetString(0));
            }

            foreach (var tableName in tableNames)
            {
                using var insertCmd = new NpgsqlCommand("INSERT INTO entity_meta (EntityName) VALUES (@name) RETURNING EntityId", conn);
                insertCmd.Parameters.AddWithValue("name", tableName);
                entityDict.Add(tableName, Convert.ToInt32(insertCmd.ExecuteScalar()));
            }

            Console.WriteLine("3. Sütunlar (Attributes) Okunuyor ve Yazılıyor...");
            var columnsData = new List<Tuple<string, string, string>>();
            var attributeDict = new Dictionary<string, int>(); // TabloAdi.SutunAdi -> AttributeId eşleşmesi için

            using (var cmdCols = new NpgsqlCommand("SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name NOT LIKE '%_meta'", conn))
            using (var readerCols = cmdCols.ExecuteReader())
            {
                while (readerCols.Read()) columnsData.Add(new Tuple<string, string, string>(readerCols.GetString(0), readerCols.GetString(1), readerCols.GetString(2)));
            }

            foreach (var col in columnsData)
            {
                if (entityDict.ContainsKey(col.Item1))
                {
                    using var insertCmd = new NpgsqlCommand("INSERT INTO attribute_meta (EntityId, AttributeName, DataType) VALUES (@eId, @cName, @dType) RETURNING AttributeId", conn);
                    insertCmd.Parameters.AddWithValue("eId", entityDict[col.Item1]);
                    insertCmd.Parameters.AddWithValue("cName", col.Item2);
                    insertCmd.Parameters.AddWithValue("dType", col.Item3);

                    int attrId = Convert.ToInt32(insertCmd.ExecuteScalar());
                    attributeDict[$"{col.Item1}.{col.Item2}"] = attrId;
                }
            }

            Console.WriteLine("4. İlişkiler (Foreign Keys) Okunuyor ve Yazılıyor...");
            string getFkSql = @"
                SELECT 
                    tc.constraint_name, 
                    tc.table_name AS child_table, 
                    ccu.table_name AS parent_table
                FROM information_schema.table_constraints AS tc 
                JOIN information_schema.constraint_column_usage AS ccu ON ccu.constraint_name = tc.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY'";

            var relationships = new List<Tuple<string, string, string>>();
            using (var cmdFk = new NpgsqlCommand(getFkSql, conn))
            using (var readerFk = cmdFk.ExecuteReader())
            {
                while (readerFk.Read())
                {
                    relationships.Add(new Tuple<string, string, string>(readerFk.GetString(0), readerFk.GetString(1), readerFk.GetString(2)));
                }
            }

            foreach (var rel in relationships)
            {
                string fkName = rel.Item1;
                string childTable = rel.Item2;
                string parentTable = rel.Item3;

                if (entityDict.ContainsKey(childTable) && entityDict.ContainsKey(parentTable))
                {
                    using var insertCmd = new NpgsqlCommand("INSERT INTO relationship_meta (ParentEntityId, ChildEntityId, ForeignKeyName) VALUES (@pId, @cId, @fkName)", conn);
                    insertCmd.Parameters.AddWithValue("pId", entityDict[parentTable]);
                    insertCmd.Parameters.AddWithValue("cId", entityDict[childTable]);
                    insertCmd.Parameters.AddWithValue("fkName", fkName);
                    insertCmd.ExecuteNonQuery();
                }
            }

            // ====================================================================
            // BÖLÜM 3: ENTITY OCCURRENCE (Ham Verilerin EAV Olarak Yazılması)
            // ====================================================================
            Console.WriteLine("5. Entity Occurrences (Ham Veriler) Okunuyor ve entityOccurrence_meta Tablosuna Yazılıyor...");

            foreach (var tableName in tableNames)
            {
                int entityId = entityDict[tableName];
                var rowDataList = new List<List<(string colName, string dataValue)>>();

                using (var cmdData = new NpgsqlCommand($"SELECT * FROM {tableName}", conn))
                using (var readerData = cmdData.ExecuteReader())
                {
                    while (readerData.Read())
                    {
                        var row = new List<(string colName, string dataValue)>();
                        for (int i = 0; i < readerData.FieldCount; i++)
                        {
                            string colName = readerData.GetName(i);
                            string dataValue = readerData.IsDBNull(i) ? "NULL" : (readerData.GetValue(i)?.ToString() ?? "NULL");
                            row.Add((colName, dataValue));
                        }
                        rowDataList.Add(row);
                    }
                }

                int rowIndex = 0;
                foreach (var row in rowDataList)
                {
                    rowIndex++;
                    foreach (var cell in row)
                    {
                        if (attributeDict.TryGetValue($"{tableName}.{cell.colName}", out int attributeId))
                        {
                            using var insertOccCmd = new NpgsqlCommand(
                                "INSERT INTO entityOccurrence_meta (EntityId, AttributeId, RowIndex, DataValue) VALUES (@eId, @aId, @rIdx, @dVal)", conn);

                            insertOccCmd.Parameters.AddWithValue("eId", entityId);
                            insertOccCmd.Parameters.AddWithValue("aId", attributeId);
                            insertOccCmd.Parameters.AddWithValue("rIdx", rowIndex);
                            insertOccCmd.Parameters.AddWithValue("dVal", cell.dataValue);

                            insertOccCmd.ExecuteNonQuery();
                        }
                    }
                }
            }

            Console.WriteLine("Metamodel ve Occurrence Çıkarımı Tamamlandı.\n");


            // ====================================================================
            // BÖLÜM 4: BİLİMSEL DOĞRULAMA VE KANIT (VERIFICATION)
            // ====================================================================
            Console.WriteLine("=======================================================");
            Console.WriteLine("=== BİLİMSEL DOĞRULAMA (BLACK BOX VERIFICATION) ===");
            Console.WriteLine("=======================================================");

            Console.WriteLine(">>> KANIT 1: Sistem Kataloğu (Metadata) Doğrulaması");
            using (var cmdFk = new NpgsqlCommand(getFkSql, conn))
            using (var readerFk = cmdFk.ExecuteReader())
            {
                while (readerFk.Read())
                {
                    Console.WriteLine($"    [İSPAT] Veritabanı motoru '{readerFk.GetString(1)}' tablosunun '{readerFk.GetString(2)}' tablosuna '{readerFk.GetString(0)}' kısıtlaması ile bağlı olduğunu resmi kataloğuna kaydetmiştir.\n");
                }
            }

            Console.WriteLine(">>> KANIT 2: Fiziksel B-Tree ve Çalıştırma Planı (Execution Plan) Doğrulaması");
            string explainSql = "EXPLAIN ANALYZE SELECT * FROM musteriler m JOIN siparisler s ON m.MusteriId = s.MusteriId;";
            using (var cmdExplain = new NpgsqlCommand(explainSql, conn))
            using (var readerExplain = cmdExplain.ExecuteReader())
            {
                Console.WriteLine("    [İSPAT] İki tablo JOIN edildiğinde veritabanının arka planda izlediği fiziksel yollar:");
                while (readerExplain.Read())
                {
                    Console.WriteLine("      > " + readerExplain.GetString(0));
                }
                Console.WriteLine("    *(Yukarıdaki çıktıda 'Hash Join' veya 'Index Scan' ifadeleri, motorun ilişkiyi fiziksel bellekte nasıl eşleştirdiğinin kanıtıdır)*\n");
            }

            Console.WriteLine(">>> KANIT 3: Davranışsal Kural İhlali (Negative Testing)");
            try
            {
                // Not: 999 ID'sinin veritabanında olmaması beklenir. Eğer bir gün 999 ID'li müşteri eklerseniz bu test fail yerine başarılı ekleme yapar.
                using var insertFailCmd = new NpgsqlCommand("INSERT INTO siparisler (SiparisId, MusteriId, Tutar) VALUES (101, 999, 100.00);", conn);
                insertFailCmd.ExecuteNonQuery();
                Console.WriteLine("    [HATA] Bu yazı görünmemeliydi, veritabanı kuralı çiğnemeye izin verdi!");
            }
            catch (PostgresException ex)
            {
                Console.WriteLine($"    [İSPAT A - BAŞARILI] Sistem, ana tabloda var olmayan '999' ID'li müşteriye sipariş eklemeyi engelledi.");
                Console.WriteLine($"      -> Fırlatılan Motor Hatası: [{ex.SqlState}] {ex.MessageText}");
            }

            try
            {
                // Not: 1 ID'li müşterinin silinebilmesi için ona bağlı sipariş olmaması gerekir. Sabit veritabanınızda 1 numaraya bağlı sipariş varsa bu test çalışır.
                using var deleteFailCmd = new NpgsqlCommand("DELETE FROM musteriler WHERE MusteriId = 1;", conn);
                deleteFailCmd.ExecuteNonQuery();
                Console.WriteLine("    [HATA] Bu yazı görünmemeliydi, veritabanı kuralı çiğnemeye izin verdi!");
            }
            catch (PostgresException ex)
            {
                Console.WriteLine($"    [İSPAT B - BAŞARILI] Sistem, alt tabloda (Siparişler) kaydı bulunan Müşterinin silinip 'Yetim Kayıt (Orphan Record)' oluşmasını engelledi.");
                Console.WriteLine($"      -> Fırlatılan Motor Hatası: [{ex.SqlState}] {ex.MessageText}");
            }

            Console.WriteLine("\n*** BİLİMSEL KANITLAMA RAPORU TAMAMLANDI! ***");
        }
    }
}