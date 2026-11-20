# Working agreements for this repo

This is my hands-on learning repo for RAG in modern .NET (Microsoft.Extensions.AI, VectorData, DataIngestion, Aspire, Qdrant, Azure OpenAI).

- **I run the commands.** Tell me what to install or run and why. Don't install tools, scaffold projects, or run `dotnet new` yourself. Reading files, building to check something, and inspecting running resources are fine.
- **You write the docs.** Keep `README.md` (and per-project READMEs) up to date as we go.
  - Link official docs instead of copying them; only write down what's specific to my setup or what we figured out.
  - Use Mermaid diagrams where a picture builds the mental model (components, sequence of a request, data flow), plus a "read the code in this order" list.
  - Keep my existing voice and style: step-by-step commands with their output, and short "why" notes.
- **Keep code simple and elegant.** Prefer the smallest design that shows best practice; easy to read, reason about and explain. No clever abstractions or reflection when a plain lambda/loop does it.
  - Comment like `RagChat.Evaluation/RagFixture.cs`: a 1–2 line purpose comment per class/method, and short "what/why" comments on non-obvious steps.
- **Explain for interviews.** When something is a design decision or trade-off (chunking, distance functions, agentic vs fixed RAG pipeline, DI lifetimes, security trimming, evaluation), call it out so I can explain it in an interview.
- One-time context from the previous session: `~/RiderProjects/azure-search-openai-demo-csharp/notes/handoff-ragchat.md`.
