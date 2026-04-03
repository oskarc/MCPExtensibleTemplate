# MCP Server Documentation

Welcome to the MCP Server Template documentation! This folder contains comprehensive guides for understanding, configuring, and extending the server.

## 📚 Documentation Files

### [01-ARCHITECTURE.md](01-ARCHITECTURE.md)
**For**: Developers who want to understand how the system works internally

**What you'll learn**:
- The 4-layer architecture (Transport, Middleware, MCP Protocol, Provider)
- How data flows through the system
- Dependency injection and service registration
- Extension points for customization
- Security model and safety mechanisms

**Best for**: Code review, architectural decisions, adding new features

---

### [02-ARCHITECTURE-FLOWCHARTS.md](02-ARCHITECTURE-FLOWCHARTS.md)
**For**: Visual learners who prefer diagrams over prose

**What you'll find**:
- 10 detailed flowcharts showing:
  - High-level system architecture
  - Complete request lifecycle
  - Startup initialization
  - Tool invocation flow
  - Error handling
  - stdio vs HTTP deployment
  - Configuration hierarchy
  - Adding a new provider

**Best for**: Onboarding, presentations, understanding at a glance

---

### [03-TESTING-STRATEGY.md](03-TESTING-STRATEGY.md)
**For**: QA engineers and developers writing tests

**What you'll learn**:
- 4 layers of testing (unit, integration, E2E, security)
- How to write tests using xUnit
- Testing best practices and patterns
- Test fixtures and mocking
- Coverage goals
- CI/CD integration

**Best for**: Writing robust tests, ensuring code quality, preventing regressions

---

### [04-CONFIGURATION.md](04-CONFIGURATION.md)
**For**: DevOps engineers and deployment specialists

**What you'll find**:
- Complete reference of all configuration keys
- Configuration hierarchy (environment variables > env-specific files > base)
- How to set up for Development, Testing, and Production
- Troubleshooting common configuration issues
- Common deployment scenarios

**Best for**: Deploying, securing, tuning for performance

---

### [05-USAGE-GUIDE-BEGINNERS.md](05-USAGE-GUIDE-BEGINNERS.md)
**For**: New users and non-technical stakeholders

**What you'll learn**:
- What is an MCP Server and why you'd use it
- Key concepts explained simply (Tools, Providers, Configuration)
- Step-by-step getting started guide
- How to run locally and on a network
- How to add your own API provider
- Debugging and FAQ

**Best for**: Getting started, understanding concepts, troubleshooting basics

---

## 🎯 Quick Navigation by Role

### **I'm a Beginner**
Start here → [05-USAGE-GUIDE-BEGINNERS.md](05-USAGE-GUIDE-BEGINNERS.md)

Then explore → [02-ARCHITECTURE-FLOWCHARTS.md](02-ARCHITECTURE-FLOWCHARTS.md) (visual overview)

---

### **I'm a Developer Building Features**
Start here → [01-ARCHITECTURE.md](01-ARCHITECTURE.md)

Then explore → [02-ARCHITECTURE-FLOWCHARTS.md](02-ARCHITECTURE-FLOWCHARTS.md) (for clarity)

Then add tests → [03-TESTING-STRATEGY.md](03-TESTING-STRATEGY.md)

---

### **I'm Testing/QA**
Start here → [03-TESTING-STRATEGY.md](03-TESTING-STRATEGY.md)

Reference → [01-ARCHITECTURE.md](01-ARCHITECTURE.md) (for understanding what to test)

---

### **I'm DevOps/Deploying**
Start here → [04-CONFIGURATION.md](04-CONFIGURATION.md)

Background → [05-USAGE-GUIDE-BEGINNERS.md](05-USAGE-GUIDE-BEGINNERS.md) (Key Concepts section)

---

### **I'm Reviewing Code**
Start here → [01-ARCHITECTURE.md](01-ARCHITECTURE.md)

Verify → [02-ARCHITECTURE-FLOWCHARTS.md](02-ARCHITECTURE-FLOWCHARTS.md)

Test coverage → [03-TESTING-STRATEGY.md](03-TESTING-STRATEGY.md)

---

## 🚀 Quick Start Checklist

- [ ] Read [Key Concepts](05-USAGE-GUIDE-BEGINNERS.md#key-concepts-simple-explanations) (5 min)
- [ ] Follow [Getting Started](05-USAGE-GUIDE-BEGINNERS.md#getting-started-step-by-step) (10 min)
- [ ] Run the server locally: `dotnet run` (2 min)
- [ ] Connect your AI assistant (VS Code or Claude Desktop) (5 min)
- [ ] Ask your AI to use a tool (e.g., "Get blog post #1") (2 min)
- [ ] ✅ You're now using an MCP Server!

---

## 📋 Document Overview Table

| Document | Purpose | Audience | Length | Visual? |
|----------|---------|----------|--------|---------|
| [05-USAGE-GUIDE-BEGINNERS](05-USAGE-GUIDE-BEGINNERS.md) | Get started & understand concepts | Everyone | ~30 min | Yes (examples) |
| [01-ARCHITECTURE](01-ARCHITECTURE.md) | Deep dive into internals | Developers | ~20 min | No (prose) |
| [02-ARCHITECTURE-FLOWCHARTS](02-ARCHITECTURE-FLOWCHARTS.md) | Visual system overview | Visual learners | ~15 min | Yes (flowcharts) |
| [03-TESTING-STRATEGY](03-TESTING-STRATEGY.md) | How to write tests | QA/Developers | ~25 min | Yes (code examples) |
| [04-CONFIGURATION](04-CONFIGURATION.md) | Configure for any environment | DevOps/Developers | ~20 min | No (references) |

---

## 🎓 Learning Paths

### Path 1: "I Want to Understand the Whole System" (60 min)
1. [Key Concepts](05-USAGE-GUIDE-BEGINNERS.md#key-concepts-simple-explanations) (5 min)
2. [System Architecture Section](02-ARCHITECTURE-FLOWCHARTS.md#1-high-level-system-architecture) (5 min)
3. [Complete Architecture Explanation](01-ARCHITECTURE.md) (20 min)
4. [Request Lifecycle Flowchart](02-ARCHITECTURE-FLOWCHARTS.md#2-request-lifecycle-complete-flow) (10 min)
5. [Testing Strategy Overview](03-TESTING-STRATEGY.md#test-layers) (10 min)
6. [Configuration Basics](04-CONFIGURATION.md#overview) (10 min)

### Path 2: "I Want to Run This Locally" (20 min)
1. [Getting Started Steps](05-USAGE-GUIDE-BEGINNERS.md#getting-started-step-by-step)
2. Done! You're running the MCP Server.

### Path 3: "I Want to Add a New Provider" (90 min)
1. [Provider Layer](01-ARCHITECTURE.md#4-provider-layer-api-integration-logic) (10 min)
2. [Adding a New Provider Flowchart](02-ARCHITECTURE-FLOWCHARTS.md#10-adding-a-new-provider-step-by-step) (5 min)
3. [Testing Strategy for New Providers](03-TESTING-STRATEGY.md#writing-tests-for-your-new-provider) (20 min)
4. [Configuration for New Providers](04-CONFIGURATION.md#provider-settings) (10 min)
5. Implement your provider (30 min)
6. Write tests (15 min)

### Path 4: "I Want to Deploy This to Production" (45 min)
1. [HTTP Transport Setup](05-USAGE-GUIDE-BEGINNERS.md#scenario-2-running-on-the-network-testing) (5 min)
2. [Complete Configuration Reference](04-CONFIGURATION.md) (25 min)
3. [Production Scenario](04-CONFIGURATION.md#scenario-3-production-deployment) (15 min)

---

## 🔍 Searching for Specific Topics

### Setup & Installation
- [Getting Started](05-USAGE-GUIDE-BEGINNERS.md#getting-started-step-by-step)
- [Prerequisites](05-USAGE-GUIDE-BEGINNERS.md#step-1-install-prerequisites)

### Running the Server
- [Local Development](05-USAGE-GUIDE-BEGINNERS.md#scenario-1-running-locally-development)
- [HTTP Mode](05-USAGE-GUIDE-BEGINNERS.md#scenario-2-running-on-the-network-testing)
- [Production](04-CONFIGURATION.md#scenario-3-production-deployment)

### Architecture & Design
- [High-Level System Architecture](02-ARCHITECTURE-FLOWCHARTS.md#1-high-level-system-architecture)
- [Complete Data Flow](02-ARCHITECTURE-FLOWCHARTS.md#2-request-lifecycle-complete-flow)
- [4-Layer Architecture](01-ARCHITECTURE.md#layered-architecture)

### Configuration
- [All Config Options](04-CONFIGURATION.md#configuration-keys-reference)
- [Environment Selection](04-CONFIGURATION.md#environment-selection)
- [Using Environment Variables](04-CONFIGURATION.md#set-environment-variables)

### Security
- [Security Model](01-ARCHITECTURE.md#security-model)
- [HTTPS Validation](04-CONFIGURATION.md#https-only-validation)
- [API Key Management](04-CONFIGURATION.md#authenticationapikey)

### Testing
- [Test Structure](03-TESTING-STRATEGY.md#current-test-structure)
- [Writing New Tests](03-TESTING-STRATEGY.md#writing-tests-for-your-new-provider)
- [Best Practices](03-TESTING-STRATEGY.md#testing-best-practices)

### Adding Providers
- [Provider Architecture](01-ARCHITECTURE.md#4-provider-layer-api-integration-logic)
- [Step-by-Step Guide](02-ARCHITECTURE-FLOWCHARTS.md#10-adding-a-new-provider-step-by-step)
- [Configuration](04-CONFIGURATION.md#provider-settings)

### Troubleshooting
- [Common Issues](05-USAGE-GUIDE-BEGINNERS.md#common-pitfalls--solutions)
- [Configuration Issues](04-CONFIGURATION.md#troubleshooting)
- [Debugging](05-USAGE-GUIDE-BEGINNERS.md#debugging-tools-for-understanding-whats-happening)

---

## 💡 Tips for Using This Documentation

1. **Start with your role**: Find your role in "Quick Navigation by Role" above
2. **Follow learning paths**: Pick a learning path that matches what you want to accomplish
3. **Use flowcharts liberally**: When prose feels unclear, check the flowcharts document
4. **Reference configs**: Use [04-CONFIGURATION.md](04-CONFIGURATION.md) as a full reference while working
5. **Copy examples**: Most documents include copy-paste examples you can adapt
6. **Search by topic**: Use "Searching for Specific Topics" above to find what you need

---

## ❓ FAQ About the Docs

**Q: Which document should I read first?**
A: If you're new: [05-USAGE-GUIDE-BEGINNERS.md](05-USAGE-GUIDE-BEGINNERS.md)
   If you're technical: [01-ARCHITECTURE.md](01-ARCHITECTURE.md)

**Q: Are these docs for the C# MCP SDK or my specific provider?**
A: These docs are about the MCP Server **template** and how to use/extend it. For C# SDK details, see the [official SDK docs](https://github.com/modelcontextprotocol/csharp-sdk).

**Q: What if I find a mistake or unclear section?**
A: File an issue or submit a PR to improve these docs!

**Q: Can I use these docs for a different MCP server implementation?**
A: Some parts are specific to this .NET template, but concepts (architecture, testing, configuration) are universal to most MCP servers.

---

## 📖 Format Notes

- 📝 Text explanations: Plain language + technical depth
- 📊 Flowcharts: Mermaid diagrams (detailed, visual)
- 💻 Code examples: C# with copy-paste ready
- ✅ Checklists: Task lists for implementation
- 📋 Tables: Reference material for lookups

---

## 🔗 External Resources

- [MCP Specification](https://modelcontextprotocol.io)
- [C# MCP SDK GitHub](https://github.com/modelcontextprotocol/csharp-sdk)
- [.NET 8 Documentation](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8)
- [Serilog Logging](https://serilog.net/)
- [OWASP Security Best Practices](https://owasp.org)

---

Last updated: April 2026
