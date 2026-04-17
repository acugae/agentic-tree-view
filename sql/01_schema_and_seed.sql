-- ============================================================
-- KNOWLEDGE BASE — Schema tabellare
-- ============================================================

-- Albero dei nodi (filesystem virtuale)
CREATE TABLE KnowledgeNode (
    Id          UNIQUEIDENTIFIER    PRIMARY KEY DEFAULT NEWID(),
    ParentId    UNIQUEIDENTIFIER    NULL REFERENCES KnowledgeNode(Id),
    Name        NVARCHAR(200)   NOT NULL,       -- nome semantico del nodo
    NodeType    NVARCHAR(10)    NOT NULL,        -- 'folder' | 'file'
    Content     NVARCHAR(MAX)   NULL,            -- NULL per le cartelle
    Tags        NVARCHAR(500)   NULL,            -- parole chiave per la ricerca
    UpdatedAt   DATETIME2       NOT NULL DEFAULT GETDATE()
);

-- Chunk opzionali (popolati solo per nodi lunghi)
CREATE TABLE KnowledgeChunk (
    Id          INT                 PRIMARY KEY IDENTITY,
    NodeId      UNIQUEIDENTIFIER    NOT NULL REFERENCES KnowledgeNode(Id) ON DELETE CASCADE,
    ChunkIndex  INT             NOT NULL,        -- ordine progressivo (0, 1, 2...)
    Heading     NVARCHAR(200)   NULL,            -- heading Markdown del chunk
    Content     NVARCHAR(MAX)   NOT NULL,
    TokenCount  INT             NULL             -- stima token (length / 4)
);

-- ============================================================
-- Dati di esempio
-- ============================================================

-- Cartelle
DECLARE @Billing            UNIQUEIDENTIFIER = '11111111-0000-0000-0000-000000000001';
DECLARE @Fatturazione       UNIQUEIDENTIFIER = '11111111-0000-0000-0000-000000000002';
DECLARE @Pagamenti          UNIQUEIDENTIFIER = '11111111-0000-0000-0000-000000000003';
DECLARE @Provisioning       UNIQUEIDENTIFIER = '11111111-0000-0000-0000-000000000004';
DECLARE @AttivazioneServizi UNIQUEIDENTIFIER = '11111111-0000-0000-0000-000000000005';

INSERT INTO KnowledgeNode (Id, ParentId, Name, NodeType) VALUES
    (@Billing,              NULL,           'Billing',              'folder'),
    (@Fatturazione,         @Billing,       'Fatturazione',         'folder'),
    (@Pagamenti,            @Billing,       'Pagamenti',            'folder'),
    (@Provisioning,         NULL,           'Provisioning',         'folder'),
    (@AttivazioneServizi,   @Provisioning,  'Attivazione Servizi',  'folder');

-- File (nodi atomici, niente chunk)
INSERT INTO KnowledgeNode (Id, ParentId, Name, NodeType, Content, Tags) VALUES
    ('11111111-0000-0000-0000-000000000006', @Fatturazione, 'Calcolo IVA su gas',
        'file',
        N'## Calcolo IVA su gas
LIVa applicata al consumo gas domestico è al 10% fino a 480 mc/anno, 22% oltre soglia.
La soglia viene calcolata su base annua per punto di fornitura.',
        N'iva,gas,calcolo,aliquota'),

    ('11111111-0000-0000-0000-000000000007', @Fatturazione, 'Storno fattura',
        'file',
        N'## Storno fattura
Lo storno viene emesso entro 30 giorni dalla segnalazione.
Genera una nota di credito collegata alla fattura originale.',
        N'storno,nota credito,fattura'),

    ('11111111-0000-0000-0000-000000000008', @Pagamenti, 'Scadenze pagamento',
        'file',
        N'## Scadenze pagamento
Le fatture hanno scadenza a 30 giorni dalla data di emissione.
Il mancato pagamento entro 60 giorni attiva il processo di sollecito automatico.',
        N'scadenza,pagamento,sollecito');
