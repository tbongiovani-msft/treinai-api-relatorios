using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TreinAI.Shared.Exceptions;
using TreinAI.Shared.Middleware;
using TreinAI.Shared.Models;
using TreinAI.Shared.Repositories;
using TreinAI.Shared.Validation;

namespace TreinAI.Api.Relatorios.Functions;

/// <summary>
/// Reporting endpoints — read-only aggregations across atividades, avaliacoes, alunos.
/// </summary>
public class RelatorioFunctions
{
    private readonly IRepository<Atividade> _atividadeRepo;
    private readonly IRepository<Avaliacao> _avaliacaoRepo;
    private readonly IRepository<Aluno> _alunoRepo;
    private readonly IRepository<Treino> _treinoRepo;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<RelatorioFunctions> _logger;

    public RelatorioFunctions(
        IRepository<Atividade> atividadeRepo,
        IRepository<Avaliacao> avaliacaoRepo,
        IRepository<Aluno> alunoRepo,
        IRepository<Treino> treinoRepo,
        TenantContext tenantContext,
        ILogger<RelatorioFunctions> logger)
    {
        _atividadeRepo = atividadeRepo;
        _avaliacaoRepo = avaliacaoRepo;
        _alunoRepo = alunoRepo;
        _treinoRepo = treinoRepo;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/relatorios/dashboard — Dashboard summary for professor/admin.
    /// </summary>
    [Function("GetDashboard")]
    public async Task<HttpResponseData> GetDashboard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "relatorios/dashboard")] HttpRequestData req)
    {
        var totalAlunos = await _alunoRepo.CountAsync(_tenantContext.TenantId);
        var totalTreinos = await _treinoRepo.CountAsync(_tenantContext.TenantId);
        var totalAtividades = await _atividadeRepo.CountAsync(_tenantContext.TenantId);
        var totalAvaliacoes = await _avaliacaoRepo.CountAsync(_tenantContext.TenantId);

        // Atividades last 7 days
        var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);
        var atividadesRecentes = await _atividadeRepo.CountAsync(
            _tenantContext.TenantId,
            a => a.DataExecucao >= sevenDaysAgo);

        // Atividades last 30 days
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        var atividadesMes = await _atividadeRepo.CountAsync(
            _tenantContext.TenantId,
            a => a.DataExecucao >= thirtyDaysAgo);

        var dashboard = new
        {
            totalAlunos,
            totalTreinos,
            totalAtividades,
            totalAvaliacoes,
            atividadesUltimos7Dias = atividadesRecentes,
            atividadesUltimos30Dias = atividadesMes,
            geradoEm = DateTime.UtcNow
        };

        return await ValidationHelper.OkAsync(req, dashboard);
    }

    /// <summary>
    /// GET /api/relatorios/aluno/{alunoId}/resumo — Student summary report.
    /// </summary>
    [Function("GetResumoAluno")]
    public async Task<HttpResponseData> GetResumoAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "relatorios/aluno/{alunoId}/resumo")] HttpRequestData req,
        string alunoId)
    {
        if (_tenantContext.IsAluno && alunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios relatórios.");

        var aluno = await _alunoRepo.GetByIdAsync(alunoId, _tenantContext.TenantId);
        if (aluno == null)
            throw new NotFoundException("Aluno", alunoId);

        var atividades = await _atividadeRepo.QueryAsync(
            _tenantContext.TenantId, a => a.AlunoId == alunoId);
        var avaliacoes = await _avaliacaoRepo.QueryAsync(
            _tenantContext.TenantId, a => a.AlunoId == alunoId);

        // Last 30 days
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        var atividadesMes = atividades.Where(a => a.DataExecucao >= thirtyDaysAgo).ToList();

        var resumo = new
        {
            aluno = new { aluno.Id, aluno.Nome, aluno.Email },
            totalAtividades = atividades.Count,
            atividadesUltimos30Dias = atividadesMes.Count,
            totalMinutosTreinados = atividades.Sum(a => a.DuracaoMinutos),
            minutosUltimos30Dias = atividadesMes.Sum(a => a.DuracaoMinutos),
            totalAvaliacoes = avaliacoes.Count,
            ultimaAvaliacao = avaliacoes.OrderByDescending(a => a.DataAvaliacao).FirstOrDefault(),
            ultimaAtividade = atividades.OrderByDescending(a => a.DataExecucao).FirstOrDefault(),
            geradoEm = DateTime.UtcNow
        };

        return await ValidationHelper.OkAsync(req, resumo);
    }

    /// <summary>
    /// GET /api/relatorios/aluno/{alunoId}/evolucao — Body composition evolution over time.
    /// </summary>
    [Function("GetEvolucaoAluno")]
    public async Task<HttpResponseData> GetEvolucaoAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "relatorios/aluno/{alunoId}/evolucao")] HttpRequestData req,
        string alunoId)
    {
        if (_tenantContext.IsAluno && alunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios relatórios.");

        var avaliacoes = await _avaliacaoRepo.QueryAsync(
            _tenantContext.TenantId, a => a.AlunoId == alunoId);

        var evolucao = avaliacoes
            .OrderBy(a => a.DataAvaliacao)
            .Select(a => new
            {
                data = a.DataAvaliacao,
                peso = a.Peso,
                percentualGordura = a.PercentualGordura,
                massaMagra = a.MassaMagra,
                imc = a.Imc
            })
            .ToList();

        return await ValidationHelper.OkAsync(req, new { alunoId, evolucao });
    }

    /// <summary>
    /// GET /api/relatorios/aluno/{alunoId}/frequencia — Training frequency report.
    /// </summary>
    [Function("GetFrequenciaAluno")]
    public async Task<HttpResponseData> GetFrequenciaAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "relatorios/aluno/{alunoId}/frequencia")] HttpRequestData req,
        string alunoId)
    {
        if (_tenantContext.IsAluno && alunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios relatórios.");

        var atividades = await _atividadeRepo.QueryAsync(
            _tenantContext.TenantId, a => a.AlunoId == alunoId);

        // Group by week
        var frequenciaSemanal = atividades
            .GroupBy(a =>
            {
                var date = a.DataExecucao.Date;
                var diff = (int)date.DayOfWeek - 1;
                if (diff < 0) diff = 6;
                return date.AddDays(-diff); // Monday of that week
            })
            .Select(g => new
            {
                semanaInicio = g.Key,
                totalTreinos = g.Count(),
                totalMinutos = g.Sum(a => a.DuracaoMinutos)
            })
            .OrderByDescending(x => x.semanaInicio)
            .Take(12)
            .ToList();

        return await ValidationHelper.OkAsync(req, new { alunoId, frequenciaSemanal });
    }
}
