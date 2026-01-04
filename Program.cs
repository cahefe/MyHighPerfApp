using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
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
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Optimized);
    //  Valores para demais entidades não catalogadas no Serializador acima
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// 3. Object Pooling:
// Configura um pool para reutilizar objetos 'ResponseData'.
// Isso alivia drasticamente a pressão sobre o Garbage Collector (GC) em altas cargas.
builder.Services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
builder.Services.AddSingleton(static serviceProvider => serviceProvider.GetRequiredService<ObjectPoolProvider>().Create(new DefaultPooledObjectPolicy<Result<ResponseData>>()));

var app = builder.Build();

app.MapGet("/process-content-ok", static (string? queryFilter = null) => new Result<ResponseData>
{
    Success = true,
    Message = "Processo concluído✅",
    Data = new ResponseData(
            Guid.NewGuid(),
            DateTime.UtcNow,
            $"Data✅! Filtro: {queryFilter ?? "Nenhum"}"
        ),
});

// Endpoint otimizado// ...existing code...
app.MapGet("/process", static async (
    ObjectPool<Result<ResponseData>> resultPool,
    HttpContext context,
    string? queryFilter = null,
    CancellationToken cancellationToken = default) =>
{
    var result = resultPool.Get();
    try
    {
        // Alocação na Stack (Struct) - Extremamente rápido e sem pressão no GC
        result.Success = true;
        result.Message = "Processo concluído✅";
        result.Data = new ResponseData(
            Guid.NewGuid(),
            DateTime.UtcNow,
            $"Data✅! Filtro: {queryFilter ?? "Nenhum"}"
        );
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(result, AppJsonSerializerContext.Optimized.ResultResponseData, cancellationToken: cancellationToken);
        return;
    }
    finally
    {
        // Devolve o objeto limpo ao pool para a próxima requisição
        resultPool.Return(result);
    }
});

// Endpoint de Banco de Dados de Autíssima Performance
// Utiliza IAsyncEnumerable para streaming direto do banco para o JSON (memória constante)
app.MapGet("/sql-stream", (IConfiguration config, int? infoId = null, string? firstName = null) =>
{
    var connectionString = config.GetConnectionString("DefaultConnection") 
                           ?? throw new InvalidCastException("Connection string 'DefaultConnection' não encontrada.");
    return StreamDataAsync(connectionString, infoId, firstName);
});

// Endpoint POST de Alta Performance
// Recebe um payload JSON, processa com zero alocação de resposta (via Pool) e responde diretamente.
app.MapPost("/submit", async (
    RequestData request,
    ObjectPool<Result<ResponseData>> resultPool,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var result = resultPool.Get();
    try
    {
        // 1. Validação "Schema-like" manual (Zero Allocation / AOT Friendly)
        if (!request.IsValid(out var validationErrors))
        {
            // Retorna 400 Bad Request com a mensagem de erro
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            result.Success = false;
            result.Error = validationErrors;
            result.Data = default;
            await context.Response.WriteAsJsonAsync(result, AppJsonSerializerContext.Optimized.ResultResponseData, cancellationToken: cancellationToken);
            return;
        }
        
        // Criação direta do Struct (Value Type).
        // Ao atribuir a result.Data, o struct é copiado para dentro do objeto pooled (Zero Allocation de referência extra).
        var data = new ResponseData(Guid.NewGuid(), DateTime.UtcNow, $"Recebido✅: {request.Name} - Ação: {request.Action} - Valor: {request.ActionValue} - DataHora: {request.ActionDateTime}", request.AdditionalInfo);
        
        result.Success = true;
        result.Message = "Processamento concluído com sucesso";
        result.Data = data;

        // 2. Resposta 201 Created com o objeto preenchido
        context.Response.StatusCode = StatusCodes.Status201Created;

        // Escrevemos diretamente para garantir que a serialização ocorra ANTES do finally
        await context.Response.WriteAsJsonAsync(result, AppJsonSerializerContext.Optimized.ResultResponseData, cancellationToken: cancellationToken);
    }
    finally
    {
        // Devolve o objeto limpo ao pool para a próxima requisição
        resultPool.Return(result);
    }
});

// Endpoint com transação atômica e suporte a CancellationToken
app.MapPost("/atomic-operation", async (
    ObjectPool<Result<ResponseData>> resultPool, 
    IConfiguration config, 
    HttpContext context, 
    CancellationToken cancellationToken) =>
{
    var connectionString = config.GetConnectionString("DefaultConnection") 
                           ?? throw new InvalidCastException("Connection string 'DefaultConnection' não encontrada.");
    
    // 1. Obtém o container (wrapper) do pool
    var result = resultPool.Get();

    try
    {
        var atomicOperationResult = await ExecuteAtomicOperationAsync(connectionString, cancellationToken);
        
        context.Response.StatusCode = StatusCodes.Status201Created;
        
        // 3. Cria o struct diretamente
        var innerData = new ResponseData(
            Guid.NewGuid(), 
            DateTime.UtcNow, 
            $"Operação atômica concluída. I:{atomicOperationResult.InsertedRows}, U:{atomicOperationResult.UpdatedRows}, D:{atomicOperationResult.DeletedRows}"
        );

        // 4. Monta a hierarquia manualmente
        result.Success = true;
        result.Message = "Operação atômica concluída com sucesso";
        result.Data = innerData;
    }
    catch (OperationCanceledException ex)
    {
        context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
        result.Success = false;
        result.Error = "Operação cancelada pelo cliente ou timeout";
        result.Details = ex.Message;
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        result.Success = false;
        result.Error = "Erro na operação atômica";
        result.Details = ex.Message;
    }
    finally
    {
        await context.Response.WriteAsJsonAsync(result, AppJsonSerializerContext.Optimized.ResultResponseData, cancellationToken: cancellationToken);
        
        // Depois devolvemos o container. O método TryReset() do Result<T> limpará a referência .Data
        resultPool.Return(result);
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
        yield return new SqlResponseData(
            InfoId: reader.GetInt32(0),
            FirstName: reader.GetString(1),
            BirthDate: reader.GetDateTime(2),
            EncryptedKey: await reader.IsDBNullAsync(3) ? null : reader.GetFieldValue<byte[]>(3));
    }
}

// Método com transação atômica e suporte a CancellationToken
static async Task<AtomicOperationResult> ExecuteAtomicOperationAsync(
    string connectionString, 
    CancellationToken cancellationToken = default)
{
    int insertedRows = 0;
    int updatedRows = 0;
    int deletedRows = 0;

    await using var conn = new SqlConnection(connectionString);
    // Passa CancellationToken ao OpenAsync
    await conn.OpenAsync(cancellationToken);

    // Inicia transação com isolamento apropriado
    using var transaction = conn.BeginTransaction(IsolationLevel.ReadCommitted);

    // ============ INSERT ============
    var insertCmd = new SqlCommand(
        "INSERT INTO [DBTest].[dbo].[DataInfo] (FirstName, BirthDate) VALUES (@firstName, @birthDate)",
        conn,
        transaction);

    insertCmd.Parameters.AddWithValue("@firstName", "João Silva");
    insertCmd.Parameters.AddWithValue("@birthDate", new DateTime(1990, 5, 15));

    // Passa CancellationToken ao comando
    await insertCmd.ExecuteNonQueryAsync(cancellationToken);
    insertedRows = 1;

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
    updatedRows = 1;

    // ============ DELETE ============
    var deleteCmd = new SqlCommand(
        "DELETE FROM [DBTest].[dbo].[DataInfo] WHERE FirstName LIKE @pattern",
        conn,
        transaction);

    deleteCmd.Parameters.AddWithValue("@pattern", "%João da Silva%");

    deletedRows = await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

    // ✅ COMMIT: Todas as operações foram bem-sucedidas
    await transaction.CommitAsync(cancellationToken);
    return new AtomicOperationResult(true, "Transação confirmada com sucesso (COMMIT)", insertedRows, updatedRows, deletedRows);
    // O bloco 'using' da transação garante o Rollback se não houver Commit.
    // O 'await using' da conexão garante o fechamento.
}

app.Run();

// --- Definições de Dados ---

public class Result<T> : IResettable
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? Details { get; set; }
    public T? Data { get; set; }

    // Garante que o objeto esteja limpo ao ser reutilizado pelo Pool
    public bool TryReset()
    {
        Success = false;
        Message = null;
        Error = null;
        Details = null;
        Data = default;
        return true;
    }
}

// DTO de Saída (Struct para evitar alocação na Heap e complexidade de Pool)
public readonly record struct ResponseData(Guid Id, DateTime Timestamp, string? Message, string? AdditionalInfo = null);

// DTO de Entrada (Record é leve e imutável)
public readonly record struct RequestData(string Name, string Action, decimal ActionValue = 0, DateTime? ActionDateTime = null, string? AdditionalInfo = null)
{
    // Método de validação otimizado (sem Reflection)
    public bool IsValid(out string? error)
    {
        if (ActionValue <= 10.5m)
        {
            error = "actionValue: deve ser maior que 10.5";
            return false;
        }

        if (!ActionDateTime.HasValue || ActionDateTime.Value.ToUniversalTime().Date < DateTime.UtcNow.Date)
        {
            error = "actionDateTime: deve ser informado e maior/igual à data atual";
            return false;
        }

        error = null;
        return true;
    }
}

// Resultado da operação atômica
public readonly record struct AtomicOperationResult(bool IsSuccess, string? Message, int InsertedRows, int UpdatedRows, int DeletedRows);

// Struct otimizado para leitura de banco (evita GC overhead)
public readonly record struct SqlResponseData(int InfoId, string FirstName, DateTime BirthDate, byte[]? EncryptedKey);

// --- Contexto JSON para AOT ---
// O compilador gera o código C# necessário para serializar ResponseData aqui.
[JsonSerializable(typeof(Result<ResponseData>))]
[JsonSerializable(typeof(IAsyncEnumerable<SqlResponseData>))]
// [JsonSerializable(typeof(ResponseData))]
[JsonSerializable(typeof(RequestData))]
// [JsonSerializable(typeof(AtomicOperationResult))]
// [JsonSerializable(typeof(SqlResponseData))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
    public static readonly AppJsonSerializerContext Optimized = new(new JsonSerializerOptions()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    });
}