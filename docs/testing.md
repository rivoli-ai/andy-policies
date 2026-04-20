# Testing

## Backend Tests

### Unit Tests

Located in `tests/Andy.Policies.Tests.Unit/`.

```bash
dotnet test tests/Andy.Policies.Tests.Unit
```

Uses:
- **xUnit** - Test framework
- **EF Core InMemory** - In-memory database for isolated tests
- **coverlet** - Code coverage collection

### Integration Tests

Located in `tests/Andy.Policies.Tests.Integration/`.

```bash
dotnet test tests/Andy.Policies.Tests.Integration
```

Uses:
- **WebApplicationFactory** - In-process API testing
- **xUnit** - Test framework

### Running All Tests

```bash
dotnet test
```

With coverage:
```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Frontend Tests

### Unit Tests (Karma/Jasmine)

```bash
cd client
npm test
```

Headless mode:
```bash
npm test -- --watch=false --browsers=ChromeHeadless
```

### E2E Tests

Located in `client-tests/e2e/`.

```bash
# TODO: Configure Playwright or Cypress
```

## Test Strategy

| Layer | Type | Framework | Database |
|-------|------|-----------|----------|
| Domain | Unit | xUnit | None |
| Services | Unit | xUnit | InMemory |
| Controllers | Integration | xUnit + WebApplicationFactory | InMemory |
| Angular | Unit | Karma/Jasmine | Mock |
| Angular | E2E | Playwright/Cypress | Real |
