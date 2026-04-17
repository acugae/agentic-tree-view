WITH CTE AS (
    SELECT Id, Name, CAST(Name AS NVARCHAR(MAX)) AS FullPath
    FROM KnowledgeNode WHERE ParentId IS NULL

    UNION ALL

    SELECT k.Id, k.Name, c.FullPath + '/' + k.Name
    FROM KnowledgeNode k
    INNER JOIN CTE c ON k.ParentId = c.Id
)
UPDATE k SET k.FullPath = c.FullPath
FROM KnowledgeNode k JOIN CTE c ON k.Id = c.Id