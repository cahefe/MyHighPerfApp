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

// Endpoint com transação atômica e suporte a CancellationToken
app.MapPost("/atomic-operation", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var connectionString = "Data Source=(localdb)\\MSSQLLocalDB;Integrated Security=True;Pooling=False;Connect Timeout=30;Encrypt=False;Trust Server Certificate=True;Application Name=MyHighPerfApp;Application Intent=ReadWrite;Command Timeout=30";
    
    try
    {
        var result = await ExecuteAtomicOperationAsync(connectionString, cancellationToken);
        return Results.Ok(new { success = true, message = "Operação atômica concluída com sucesso", data = result });
    }
    catch (OperationCanceledException ex)
    {
        context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
        return Results.Json(
            new { success = false, error = "Operação cancelada pelo cliente ou timeout", details = ex.Message },
            statusCode: StatusCodes.Status408RequestTimeout
        );
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return Results.Json(
            new { success = false, error = "Erro na operação atômica", details = ex.Message },
            statusCode: StatusCodes.Status500InternalServerError
        );
    }
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

// Método com transação atômica e suporte a CancellationToken
static async Task<AtomicOperationResult> ExecuteAtomicOperationAsync(
    string connectionString, 
    CancellationToken cancellationToken = default)
{
    var result = new AtomicOperationResult();
    
    await using var conn = new SqlConnection(connectionString);
    // Passa CancellationToken ao OpenAsync
    await conn.OpenAsync(cancellationToken);

    // Inicia transação com isolamento apropriado
    using var transaction = conn.BeginTransaction(IsolationLevel.ReadCommitted);
    
    try
    {
        // ============ INSERT ============
        var insertCmd = new SqlCommand(
            "INSERT INTO [DBTest].[dbo].[DataInfo] (FirstName, BirthDate) VALUES (@firstName, @birthDate)",
            conn, 
            transaction);
        
        insertCmd.Parameters.AddWithValue("@firstName", "João Silva");
        insertCmd.Parameters.AddWithValue("@birthDate", new DateTime(1990, 5, 15));
        
        // Passa CancellationToken ao comando
        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        result.InsertedRows = 1;
        
        // Simula operação demorada que pode ser cancelada
        await Task.Delay(100, cancellationToken);

        // ============ UPDATE ============
        var updateCmd = new SqlCommand(
            "UPDATE [DBTest].[dbo].[DataInfo] SET FirstName = @newName WHERE FirstName = @oldName",
            conn,
            transaction);
        
        updateCmd.Parameters.AddWithValue("@newName", "João da Silva");
        updateCmd.Parameters.AddWithValue("@oldName", "João Silva");
        
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        result.UpdatedRows = 1;

        // ============ DELETE ============
        var deleteCmd = new SqlCommand(
            "DELETE FROM [DBTest].[dbo].[DataInfo] WHERE FirstName LIKE @pattern",
            conn,
            transaction);
        
        deleteCmd.Parameters.AddWithValue("@pattern", "%Teste%");
        
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        result.DeletedRows = 0; // Provavelmente nenhuma linha

        // ✅ COMMIT: Todas as operações foram bem-sucedidas
        await transaction.CommitAsync(cancellationToken);
        result.IsSuccess = true;
        result.Message = "Transação confirmada com sucesso (COMMIT)";
    }
    catch (OperationCanceledException)
    {
        // ❌ ROLLBACK automático em caso de cancelamento
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ignora cancellamento durante rollback para garantir limpeza
            transaction.Rollback();
        }
        
        result.IsSuccess = false;
        result.Message = "Transação cancelada e revertida (ROLLBACK)";
        throw;
    }
    catch (Exception ex)
    {
        // ❌ ROLLBACK automático em caso de erro
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            transaction.Rollback();
        }
        
        result.IsSuccess = false;
        result.Message = $"Transação revertida (ROLLBACK) - Erro: {ex.Message}";
        throw;
    }
    finally
    {
        // Garante limpeza de recursos
        await conn.CloseAsync();
    }

    return result;
}

app.Run();

// --- Definições de Dados ---

public class ResponseData
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Message { get; set; }
}

// Resultado da operação atômica
public class AtomicOperationResult
{
    public bool IsSuccess { get; set; }
    public string? Message { get; set; }
    public int InsertedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int DeletedRows { get; set; }
}

// Struct otimizado para leitura de banco (evita GC overhead)
public readonly record struct SqlResponseData(int InfoId, string FirstName, DateTime BirthDate, byte[]? EncryptedKey);

// --- Contexto JSON para AOT ---
// O compilador gera o código C# necessário para serializar ResponseData aqui.
[JsonSerializable(typeof(ResponseData))]
[JsonSerializable(typeof(IAsyncEnumerable<SqlResponseData>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
