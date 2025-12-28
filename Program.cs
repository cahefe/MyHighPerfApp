using System.Text.Json.Serialization;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.ObjectPool;

// 1. CreateSlimBuilder: Carrega apenas o essencial do Kestrel e Hosting.
// Ignora configurações de IIS, validações pesadas e suporte a arquivos estáticos complexos.
var builder = WebApplication.CreateSlimBuilder(args);

// 2. JSON Source Generator:
// Registra o contexto de serialização gerado em tempo de compilação.
// Isso elimina o custo de Reflection durante a serialização/deserialização.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// 3. Object Pooling:
// Configura um pool para reutilizar objetos 'ResponseData'.
// Isso alivia drasticamente a pressão sobre o Garbage Collector (GC) em altas cargas.
builder.Services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
builder.Services.AddSingleton(serviceProvider =>
{
    var provider = serviceProvider.GetRequiredService<ObjectPoolProvider>();
    // Política padrão: cria novo se vazio, reseta ao devolver.
    return provider.Create(new DefaultPooledObjectPolicy<ResponseData>());
});

var app = builder.Build();

// Endpoint otimizado// ...existing code...
app.MapGet("/process", (ObjectPool<ResponseData> pool, string? queryFilter) =>
{
    // Obtém objeto do pool (evita 'new ResponseData()')
    var data = pool.Get();

    try
    {
        data.Id = Guid.NewGuid();
        data.Timestamp = DateTime.UtcNow;
        data.Message = $"Processado com alta performance em Kubernetes. Filtro: {queryFilter ?? "Nenhum filtro aplicado"}"; // Usando o parâmetro queryFilter

        // Retorna o resultado. O framework usa o Source Generator configurado acima.
        return Results.Ok(data);
    }
    finally
    {
        // CRÍTICO: Devolve o objeto ao pool para ser reutilizado na próxima request.
        pool.Return(data);
    }
});

// Endpoint de Banco de Dados de Autíssima Performance
// Utiliza IAsyncEnumerable para streaming direto do banco para o JSON (memória constante)
app.MapGet("/sql-stream", (int? infoId = null, string? firstName = null) =>
{
    var connectionString = "Data Source=(localdb)\\MSSQLLocalDB;Integrated Security=True;Pooling=False;Connect Timeout=30;Encrypt=False;Trust Server Certificate=True;Application Name=MyHighPerfApp;Application Intent=ReadWrite;Command Timeout=30";
    return StreamDataAsync(connectionString, infoId, firstName);
});

static async IAsyncEnumerable<SqlResponseData> StreamDataAsync(string connectionString, int? infoId = null, string? firstName = null)
{
    string sqlQuery = "SELECT [InfoId], [FirstName], [BirthDate], [EncryptedKeyNumber] FROM [DBTest].[dbo].[DataInfo] WHERE (@InfoId IS NULL OR [InfoId] = @InfoId) AND (@FirstName IS NULL OR [FirstName] LIKE '%' + ISNULL(@FirstName,'') + '%') ORDER BY [InfoId] ASC";

    // 'await using' garante o fechamento da conexão assim que o streaming terminar
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new SqlCommand(sqlQuery, conn);
    cmd.CommandType = CommandType.Text;
    cmd.Parameters.AddWithValue("@InfoId", (object?)infoId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@FirstName", (object?)firstName ?? DBNull.Value);
    
    // SequentialAccess: Lê o stream de dados sequencialmente sem carregar a linha toda em memória
    await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

    while (await reader.ReadAsync())
    {
        // Retorna um struct (Value Type) para evitar alocação na Heap
        yield return new SqlResponseData(reader.GetInt32(0), reader.GetString(1), (DateTime)reader[2], (byte[]?)reader[3]);
    }
}

app.Run();

// --- Definições de Dados ---

public class ResponseData
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Message { get; set; }
}

// Struct otimizado para leitura de banco (evita GC overhead)
public readonly record struct SqlResponseData(int InfoId, string FirstName, DateTime BirthDate, byte[]? EncryptedKey);

// --- Contexto JSON para AOT ---
// O compilador gera o código C# necessário para serializar ResponseData aqui.
[JsonSerializable(typeof(ResponseData))]
[JsonSerializable(typeof(IAsyncEnumerable<SqlResponseData>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
