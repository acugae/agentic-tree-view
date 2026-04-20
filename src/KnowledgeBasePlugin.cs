using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace OneAssistant.Plugins;

/// <summary>
/// Plugin Semantic Kernel per la Knowledge.
///
/// STRUTTURA TABELLARE:
///   KnowledgeNode  → albero dei nodi (folder/file), sempre presente
///   KnowledgeChunk → chunk opzionali, popolati solo per nodi con contenuto lungo
///
/// TOOL ESPOSTI ALL'AGENTE (3 funzioni):
///   1. GetKnowledgeMap  → mappa flat di tutto l'albero (primo passo sempre)
///   2. ReadNode         → legge un nodo; se ha chunk restituisce l'indice,
///                         altrimenti restituisce il contenuto direttamente
///   3. SearchNodes      → ricerca testuale trasversale su contenuto e tag
///
/// FLUSSO AGENTE — nodo atomico (caso tipico):
///   GetKnowledgeMap → ReadNode(id) → risposta
///
/// FLUSSO AGENTE — nodo con chunk (nodo lungo):
///   GetKnowledgeMap → ReadNode(id) [restituisce indice chunk]
///                   → ReadNode(id, chunkIndex) → risposta
/// </summary>
public class KnowledgeBasePlugin
{
    private readonly string _connectionString;

    public KnowledgeBasePlugin(string connectionString)
    {
        _connectionString = connectionString;
    }

    // =========================================================================
    // TOOL 1 — GetKnowledgeMap
    // =========================================================================

    [KernelFunction]
    [Description(
        "Restituisce la mappa completa della Knowledge Base come lista di path. " +
        "Mostra tutti i nodi disponibili (cartelle e file) con il loro ID e percorso. " +
        "Chiamare SEMPRE come primo passo per capire quali argomenti sono disponibili " +
        "e ottenere gli ID necessari per ReadNode e SearchNodes.")]
    public async Task<string> GetKnowledgeMap()
    {
        const string sql = """
            WITH CTE AS (
                SELECT  Id,
                        NodeType,
                        CAST(Name AS NVARCHAR(MAX)) AS FullPath,
                        0 AS Depth
                FROM    KnowledgeNode
                WHERE   ParentId IS NULL
                UNION ALL
                SELECT  k.Id,
                        k.NodeType,
                        c.FullPath + N'/' + k.Name,
                        c.Depth + 1
                FROM    KnowledgeNode k
                INNER JOIN CTE c ON k.ParentId = c.Id
            )
            SELECT Id, NodeType, FullPath, Depth
            FROM   CTE
            ORDER  BY FullPath
            """;

        var sb = new StringBuilder();
        sb.AppendLine("# Knowledge Base — Mappa");
        sb.AppendLine();

        await using var conn   = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var id       = reader.GetGuid(0);
            var nodeType = reader.GetString(1);
            var fullPath = reader.GetString(2);
            var depth    = reader.GetInt32(3);

            var indent = new string(' ', depth * 2);
            var icon   = nodeType == "folder" ? "📁" : "📄";

            sb.AppendLine($"[{id}] {indent}{icon} {fullPath}");
        }

        return sb.ToString();
    }

    // =========================================================================
    // TOOL 2 — ReadNode (ibrido)
    // - Senza chunkIndex → nodo atomico: contenuto diretto
    //                    → nodo con chunk: indice delle sezioni
    // - Con chunkIndex   → legge la sezione specifica
    // =========================================================================

    [KernelFunction]
    [Description(
        "Legge il contenuto di un nodo file della Knowledge Base. " +
        "Se il nodo ha sezioni (chunk), restituisce prima l'indice delle sezioni disponibili: " +
        "in quel caso richiamare ReadNode passando anche il chunkIndex desiderato. " +
        "Se il nodo è atomico, restituisce direttamente il contenuto completo. " +
        "Non chiamare su nodi di tipo 'folder'.")]
    public async Task<string> ReadNode(
        [Description("ID (Guid) del nodo da leggere, ottenuto da GetKnowledgeMap")]
        Guid nodeId,
        [Description("Indice della sezione da leggere (0, 1, 2...). " +
                     "Passare solo se ReadNode ha già restituito un indice di sezioni.")]
        int? chunkIndex = null)
    {
        if (chunkIndex.HasValue)
            return await ReadChunkAsync(nodeId, chunkIndex.Value);

        var chunkCount = await GetChunkCountAsync(nodeId);

        if (chunkCount > 0)
            return await GetNodeIndexAsync(nodeId, chunkCount);

        return await ReadNodeContentAsync(nodeId);
    }

    // =========================================================================
    // TOOL 3 — SearchNodes
    // =========================================================================

    [KernelFunction]
    [Description(
        "Cerca un termine nel contenuto e nei tag di tutti i file della Knowledge Base. " +
        "Usare quando non si sa in quale nodo si trova l'informazione, " +
        "o per ricerche trasversali su più argomenti. " +
        "Restituisce ID, path e un estratto del testo attorno alla corrispondenza. " +
        "Usare l'ID restituito con ReadNode per leggere il contenuto completo.")]
    public async Task<string> SearchNodes(
        [Description("Termine o frase da cercare nel contenuto e nei tag dei file")]
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Specificare un termine di ricerca valido.";

        const string sql = """
            WITH CTE AS (
                SELECT  Id, CAST(Name AS NVARCHAR(MAX)) AS FullPath
                FROM    KnowledgeNode WHERE ParentId IS NULL
                UNION ALL
                SELECT  k.Id, c.FullPath + N'/' + k.Name
                FROM    KnowledgeNode k INNER JOIN CTE c ON k.ParentId = c.Id
            )
            SELECT  n.Id,
                    cte.FullPath,
                    n.Content,
                    n.Tags
            FROM    KnowledgeNode n
            INNER JOIN CTE cte ON n.Id = cte.Id
            WHERE   n.NodeType = 'file'
              AND  (n.Content LIKE N'%' + @Query + N'%'
                OR  n.Tags    LIKE N'%' + @Query + N'%')
            ORDER  BY cte.FullPath
            """;

        await using var conn   = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd    = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Query", query);
        await using var reader = await cmd.ExecuteReaderAsync();

        var sb    = new StringBuilder();
        int count = 0;

        while (await reader.ReadAsync())
        {
            count++;
            var id       = reader.GetGuid(0);
            var fullPath = reader.GetString(1);
            var content  = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var tags     = reader.IsDBNull(3) ? "" : reader.GetString(3);

            sb.AppendLine($"## [{id}] {fullPath}");

            var excerpt = ExtractExcerpt(content, query, contextLength: 150);
            if (!string.IsNullOrEmpty(excerpt))
            {
                sb.AppendLine("**Estratto:**");
                sb.AppendLine($"> {excerpt}");
            }

            if (!string.IsNullOrEmpty(tags))
                sb.AppendLine($"**Tag:** {tags}");

            sb.AppendLine();
        }

        if (count == 0)
            return $"Nessun file trovato contenente \"{query}\". " +
                   "Prova con un termine più generico o usa GetKnowledgeMap per esplorare la struttura.";

        sb.Insert(0, $"# Risultati ricerca: \"{query}\"\n" +
                     $"Trovati {count} file. Usa ReadNode(id) per leggere il contenuto completo.\n\n");

        return sb.ToString();
    }

    // =========================================================================
    // Metodi privati
    // =========================================================================

    private async Task<int> GetChunkCountAsync(Guid nodeId)
    {
        const string sql = "SELECT COUNT(*) FROM KnowledgeChunk WHERE NodeId = @NodeId";
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@NodeId", nodeId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadNodeContentAsync(Guid nodeId)
    {
        const string sql = """
            WITH CTE AS (
                SELECT  Id, CAST(Name AS NVARCHAR(MAX)) AS FullPath
                FROM    KnowledgeNode WHERE ParentId IS NULL
                UNION ALL
                SELECT  k.Id, c.FullPath + N'/' + k.Name
                FROM    KnowledgeNode k INNER JOIN CTE c ON k.ParentId = c.Id
            )
            SELECT  n.NodeType, cte.FullPath, n.Content, n.Tags, n.UpdatedAt
            FROM    KnowledgeNode n
            INNER JOIN CTE cte ON n.Id = cte.Id
            WHERE   n.Id = @NodeId
            """;

        await using var conn   = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd    = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@NodeId", nodeId);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return $"Nodo con ID {nodeId} non trovato.";

        if (reader.GetString(0) == "folder")
            return $"Il nodo [{nodeId}] è una cartella. Usa GetKnowledgeMap per esplorarne i figli.";

        var fullPath  = reader.GetString(1);
        var content   = reader.IsDBNull(2) ? "(contenuto vuoto)" : reader.GetString(2);
        var tags      = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var updatedAt = reader.GetDateTime(4).ToString("dd/MM/yyyy HH:mm");

        var sb = new StringBuilder();
        sb.AppendLine($"# {fullPath}");
        sb.AppendLine($"> ID: {nodeId} | Aggiornato: {updatedAt}" +
                      (string.IsNullOrEmpty(tags) ? "" : $" | Tag: {tags}"));
        sb.AppendLine();
        sb.AppendLine(content);
        return sb.ToString();
    }

    private async Task<string> GetNodeIndexAsync(Guid nodeId, int chunkCount)
    {
        const string sql = """
            SELECT ChunkIndex, Heading, TokenCount
            FROM   KnowledgeChunk
            WHERE  NodeId = @NodeId
            ORDER  BY ChunkIndex
            """;

        await using var conn   = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd    = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@NodeId", nodeId);
        await using var reader = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"Il nodo [{nodeId}] contiene {chunkCount} sezioni. " +
                      "Richiama ReadNode(nodeId, chunkIndex) per leggere la sezione desiderata.");
        sb.AppendLine();
        sb.AppendLine("| ChunkIndex | Sezione | Token stimati |");
        sb.AppendLine("|---|---|---|");

        while (await reader.ReadAsync())
        {
            var idx     = reader.GetInt32(0);
            var heading = reader.IsDBNull(1) ? "(introduzione)" : reader.GetString(1);
            var tokens  = reader.IsDBNull(2) ? "-" : reader.GetInt32(2).ToString();
            sb.AppendLine($"| {idx} | {heading} | {tokens} |");
        }

        return sb.ToString();
    }

    private async Task<string> ReadChunkAsync(Guid nodeId, int chunkIndex)
    {
        const string sql = """
            SELECT Heading, Content
            FROM   KnowledgeChunk
            WHERE  NodeId = @NodeId AND ChunkIndex = @ChunkIndex
            """;

        await using var conn   = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd    = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@NodeId",     nodeId);
        cmd.Parameters.AddWithValue("@ChunkIndex", chunkIndex);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return $"Chunk {chunkIndex} del nodo [{nodeId}] non trovato.";

        var heading = reader.IsDBNull(0) ? null : reader.GetString(0);
        var content = reader.GetString(1);

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(heading))
            sb.AppendLine($"**Sezione:** {heading}");
        sb.AppendLine();
        sb.AppendLine(content);
        return sb.ToString();
    }

    private static string ExtractExcerpt(string content, string query, int contextLength)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var idx = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;

        var start   = Math.Max(0, idx - contextLength);
        var end     = Math.Min(content.Length, idx + query.Length + contextLength);
        var excerpt = content[start..end].Replace('\n', ' ').Trim();

        if (start > 0)             excerpt = "..." + excerpt;
        if (end < content.Length)  excerpt += "...";

        return excerpt;
    }
}
