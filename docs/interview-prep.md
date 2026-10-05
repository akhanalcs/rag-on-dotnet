# RAG interview prep

Answers I should be able to give and defend. Each answer states the trade-off, not just the choice. Every claim here should match what I actually built, either at work or in this repo.

## "Tell me about your RAG system" (the story)

> Before saying this, check every line against what I actually built, and change anything that isn't true. Say "I", give real numbers, and stop after about two minutes so they can ask follow-ups.

**Short version (≈30 seconds)**
"I built a RAG knowledge base at Marathon on .NET 10. A scheduled Azure Function pulls ServiceNow knowledge articles and resolved tickets, embeds them with Azure OpenAI, and stores them in Qdrant with source metadata. Engineers ask questions in a Blazor app built on Microsoft.Extensions.AI and get answers that link straight to the KB article or ticket. It cut the time to find information by about 90%. The next step was documents (PDF policies, procedures, manuals), and that's where citations got harder. That pushed me to hybrid search, reranking, and page-level citations, which I think is exactly the problem a law firm has."

**Full version (≈2 minutes): v1 → what I learned → v2**

*v1: ServiceNow (what's on my resume)*
- **Ingestion:** a timer-triggered Azure Function calls the ServiceNow Table API and pulls only records changed since the last run (a watermark on `sys_updated_on`). It runs separately from the web app, so a slow ingestion run never affects chat.
- **Idempotent:** the chunk key comes from the record's `sys_id` plus the chunk's position, and a content hash skips unchanged records. Re-running never creates duplicates, and retired articles get their chunks deleted.
- **Tickets as documents:** one resolved ticket becomes one document in the form *problem → diagnosis → resolution*: short description, description, filtered work notes, close notes. PII is redacted before embedding. The ticket number, type, assignment group and CI are stored as metadata.
- **Citations are easy here:** every chunk carries its ServiceNow URL, so a citation links to the exact KB article or ticket.
- **Security:** answers are trimmed by the user's groups at retrieval time, inside the vector search filter. (Our code of conduct separates departments; for example, an oil-extraction user must not see transportation-logistics operations data.) The group list is captured in code, never passed as a tool parameter, so a prompt can't widen it.
- **Quality:** a golden set of real questions, each paired with the document that should answer it. In CI, one check confirms the right document was retrieved, and LLM-as-judge scores (`Microsoft.Extensions.AI.Evaluation`) measure relevance and groundedness. A drop in quality fails the build.

*What changed: documents (PDFs)*
- A ServiceNow record **is** its own citation: one URL. A 60-page PDF isn't. "See policy.pdf" doesn't help anyone, and the user needs **the page and the passage**.
- **Plain vector search misses exact terms**: policy numbers, section numbers, error codes, names. Meaning-based search alone ranks them poorly.
- **Scanned PDFs** have no text layer, so a normal PDF parser returns nothing for them.

*v2: what I'm building for documents*
- **Reader:** Azure AI Document Intelligence (`prebuilt-layout`). OCR for scans, plus every paragraph with its **page number** and **role** (section heading, header and footer are dropped).
- **Chunking:** whole paragraphs only, up to about 500 tokens, with a new chunk at each section heading and a small overlap. Each chunk stores its **page** and **section path**.
- **Search: Azure AI Search, one query that does:**
  - **hybrid** search: BM25 keyword + vector, merged with Reciprocal Rank Fusion
  - the **semantic ranker** re-scores the top results
  - an extractive **caption**: the exact sentence that answers the question
  - the security filter, applied inside the index
- **Citations: receipts, not quotes.**
  - Each result gets an id, and the model cites ids (`[3]`).
  - The UI resolves `[3]` to the stored chunk and opens the original PDF **at that page with the passage highlighted**.
  - The model never writes the quote, so it can't misquote the source.
  - Source files are served through an endpoint that re-checks permissions.

**Why I moved from Qdrant to Azure AI Search for v2**
"Qdrant was the right choice for v1: semantic Q&A over IT content, cheap, simple. For documents I needed exact-term recall and reranking, and Azure AI Search does hybrid search plus a reranker in one query. It also brings Entra ID, private endpoints and customer-managed keys, which matter in regulated environments. I picked the tool for the requirement, not out of habit."

**Why it fits Milbank:** "In legal work, 'show me exactly where it says that' is the requirement. Ethical walls have to be enforced in retrieval, not in the prompt. Quality has to be measured, not assumed. That's how I designed it."

**Likely follow-ups, with short answers**
| Question | Answer |
|---|---|
| Why not just tell the model "don't reveal X"? | Prompts aren't security. Forbidden chunks are filtered out before the model sees anything, and the source files go through the same check. |
| How do you know answers are correct? | A golden set in CI: a deterministic retrieval check plus groundedness and relevance scored by an LLM judge. Plus citations a user can verify in one click. |
| What happens when a model is retired? | The app calls a deployment name, so only the infrastructure config changes. For an *embedding* model, I re-embed into a new index and swap, which is cheap because ingestion is idempotent and automated. |
| Chunk size? | Structure first (sections, ticket fields), then a token budget of about 300–800, tuned against the golden set. Large chunks dilute the embedding; small ones lose context. |
| Why does hybrid matter? | Vectors find meaning ("terminate for cause"); BM25 finds exact strings ("§ 9.3", "INC0012345"). Legal and IT text needs both. |
| Cost? | Embeddings cost about $0.02 per 1M tokens, and a mini chat model about $0.002 per question. The content hash avoids paying to re-process unchanged documents. |

## Model choices

### Why `text-embedding-3-small`?
- **Good enough quality for the domain.** IT knowledge articles, tickets and policy documents are plain business English. On retrieval benchmarks, `-small` sits close to `-large`.
- **Cost:** about $0.02 per 1M tokens, roughly 6.5× cheaper than `text-embedding-3-large`. Re-embedding the whole corpus, for example after changing the chunking or the model, stays cheap, which makes it easier to iterate.
- **Storage and speed:** 1536 dimensions vs 3072 for `-large`. That halves the vector storage and makes similarity search faster.
- **Decided with data, not by default.** I'd build a small "golden set" of real questions, each paired with the article or ticket that answers it. I measure **recall@k**: is the right chunk in the top 5? If `-small` and `-large` score about the same, take the cheaper one. Switch only if the eval shows a real gap.
- **Plan for change.** The embedding model is part of the index's identity. Changing it means re-embedding everything, because vectors from different models can't be compared. Ingestion is idempotent and automated, so a re-embed is just one job run into a new collection, followed by a swap.

### Why a "mini" chat model?
- In RAG, the model mostly **reads the retrieved text and summarizes it**; it doesn't need to bring knowledge of its own. A mini model handles that well, at a fraction of the cost and latency of a larger one.
- It needs reliable **tool calling**, because the model decides when to search.
- **SKU:** Global Standard is cheapest. Choose Data Zone Standard or Standard when processing must stay in the US, as with client confidentiality.
- The app asks for the **deployment name** (`chat`), not a specific model. When a model is retired, swapping it is a configuration change.

## Chunk size: how I decide

**Tokens:** the unit an LLM reads and writes. A token is a word, part of a word, or punctuation. In English, roughly **100 tokens ≈ 75 words**. Prices, context windows and chunk sizes are all measured in tokens.

**Too small** (e.g. 100 tokens):
- A chunk lacks the surrounding context needed to answer ("Restart the service." Which service? Why?).
- You need more chunks per answer, and the model has to stitch them together.

**Too large** (e.g. 3000 tokens):
- **The embedding gets diluted.** One vector has to represent many topics, so it matches none of them strongly, and retrieval precision drops. *This is the main reason, more than the context window.*
- **Cost and latency:** every question sends top-k × chunk size tokens to the model.
- **Lost in the middle:** models attend less reliably to text buried in long contexts.
- Context window limits matter too: question + system prompt + retrieved chunks + conversation history + room for the answer must all fit. But with 128K+ windows, cost and precision usually become the constraint first.

**What I use:**
- **Structure first:** split on headings or sections, and on ticket fields and comment entries, so chunks follow the document's natural meaning.
- **Then a token budget:** about **300–800 tokens** per chunk, with **10–15% overlap**, so a thought split at a boundary still appears whole in one chunk.
- **Never split a table mid-row.**
- **Validate with the golden set.** Chunk size is a tuning knob, not a guess.

**For reference:** this app's chunker (`Microsoft.Extensions.DataIngestion`) defaults to 2000 max tokens with 500 overlap. That's on the large side for precise retrieval.

## ServiceNow RAG: design deep-dive

**Goal:** engineers ask "How do I fix X?" and get a cited answer drawn from KB articles and similar resolved tickets.

```mermaid
flowchart LR
    SN[(ServiceNow Table API)] -->|delta since watermark| F[Timer Azure Function]
    F --> N[Normalize: HTML→text, redact PII]
    N --> CH[Chunk by structure + token budget]
    CH --> E[Embed: text-embedding-3-small]
    E --> Q[(Qdrant: vectors + metadata)]
    Q --> API[Blazor chat: MEAI + search tool]
    API -->|citations = ServiceNow URLs| U[Engineer]
```

### Ingestion (scheduled, incremental)
- **Timer-triggered Azure Function** (e.g. every 15 minutes). It's separate from the web app, so a slow or failing ingestion never affects chat.
- **ServiceNow Table API**, reading only what changed: `sysparm_query=sys_updated_on>{lastWatermark}`, paged with `sysparm_limit` and `sysparm_offset`. The watermark is stored after each successful run, so the job is restartable and catches up after an outage.
- **Stable IDs:** the document ID is the record's `sys_id`. The chunk key is a deterministic GUID made from `sys_id + chunk index`. Re-running **replaces** chunks instead of duplicating them.
- **Content hash:** skip re-embedding when the normalized text hasn't changed. Updates in ServiceNow often only touch fields we don't index.
- **Changes and deletes:**
  - An article is updated → delete its old chunks and insert the new ones.
  - An article is retired, unpublished, or past its `valid_to` date → delete its chunks.
  - A ticket is reopened, or new comments arrive → rebuild that ticket's document.
- **Auth to ServiceNow:** OAuth client credentials, with the secret in Key Vault, accessed via managed identity. The integration user is read-only and scoped to the tables we need.

### KB articles (`kb_knowledge`)
- Convert the HTML body to clean text or Markdown, and keep the headings.
- Chunk **by heading**, then by token budget. Put the heading path in `context`, e.g. `VPN Troubleshooting > Mac > Certificate errors`.
- **Metadata:** `number` (KB0012345), `title`, `kb_knowledge_base`, `kb_category`, `sys_updated_on`, `valid_to`, `workflow_state`, `url`, and access groups (from the KB's user criteria).

### Tickets (INC, REQ, RITM, CHG)
- **Index only resolved or closed tickets that have a meaningful resolution.** Open tickets don't contain answers yet.
- **One ticket = one logical document**, written as **Problem → Diagnosis → Resolution**:
  - header: number, short description, CI or service, category, assignment group
  - `description`
  - the relevant `work_notes` and `comments` from the `sys_journal_field` journal, in chronological order, with noise filtered out ("Any update?", auto-notifications, SLA messages)
  - `close_notes` (the resolution), which is the most valuable part
- **Most tickets fit in one chunk.** For long tickets, split by journal entries and repeat the ticket header in `context`, so every chunk still knows which ticket it belongs to.
- **Optional enrichment:** use an LLM to summarize each ticket into a clean Problem / Cause / Fix record before embedding. Retrieval improves a lot, at a small one-time cost per ticket. I'd measure the gain on the golden set before turning it on everywhere.
- **PII redaction before embedding:** names, emails, phone numbers and credentials pasted into tickets. Use regex plus a PII detector (e.g. Azure AI Language PII).
- **Metadata:** `number`, `type` (INC/REQ/RITM/CHG), `state`, `priority`, `cmdb_ci`/service, `assignment_group`, `opened_at`, `resolved_at`, `url`.

**Why store ticket numbers as metadata?**
- Users type exact identifiers ("INC0012345", error codes). Vector search is weak at exact matches.
- Index the number as a filterable field, and use **hybrid search** (keyword + vector) so exact IDs and error strings rank first.
- Citations show the number and link straight to the record in ServiceNow.

### Screenshots and attachments (`sys_attachment`)
- **Default:** don't embed images. Link to the attachment from the ticket's citation.
- **If screenshots carry the answer**, e.g. error dialogs:
  - Run OCR (Document Intelligence `prebuilt-read`) or have a vision-capable model write a caption.
  - Embed that text as a chunk with `source = attachment` and a link to the image.
- It costs more per image, so I'd do it only for ticket categories where it measurably helps.

### Retrieval and answers
- The search tool filters by the **user's groups** (security trimming), plus optional filters for type, service, or recency.
- Retrieve the top-k across KBs and tickets. Prefer KB articles (curated) over tickets (anecdotal) when they conflict. Show the source type in the citation.
- Answers cite the KB or ticket number with its ServiceNow URL. If nothing relevant is found, say so; don't guess.

### Quality and operations
- **Evaluation:** the golden set (real questions → the expected KB or ticket), measured for retrieval recall@k, plus groundedness and relevance (`Microsoft.Extensions.AI.Evaluation`). Run it in CI whenever chunking, prompts or models change.
- **Observability:** OpenTelemetry traces (tool call → embedding → vector search → completion), token usage per request, and a cost dashboard.
- **Feedback:** thumbs up/down per answer, which feeds new golden-set items.

## What a law-firm RAG needs (beyond the basics)
Covered by the build plan:
1. Idempotent, scheduled ingestion (Azure Function).
2. Provenance: page, region, and section heading per chunk (Document Intelligence).
3. Security trimming (ethical walls) at retrieval, **and** on the source-file endpoint.
4. Chunk-ID citations that open the exact page and highlight the passage.

Also expected, and worth naming:

5. **Hybrid search + reranking.** Legal text depends on exact terms (case names, statute sections, matter numbers). Vector search alone misses them.
6. **Evaluation** in CI: retrieval recall, groundedness, and relevance.
7. **Guardrails:** content safety, prompt-injection defenses (retrieved text is *data*, not instructions), and "answer only from sources".
8. **Audit logging:** who asked what, which documents were retrieved and shown. Firms need this for compliance.
9. **Data residency:** Data Zone or Standard SKUs, with no training on customer data (Azure OpenAI's default).
10. **Retention and deletion:** when a matter closes or a document is deleted, its chunks are removed too.

**Mixing API sources and files is the strong story:** "I built ingestion connectors for both structured API data (ServiceNow) and unstructured documents (PDF, Word), normalized them into one chunk schema with provenance and access metadata, and served them through one permission-aware search tool."
