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

    /// <summary>
    /// GET /api/relatorios/aluno/{alunoId}/aderencia — Adherence % (E7-12).
    /// Calculates: (atividades concluídas / total semanas × frequência prescrita) × 100.
    /// </summary>
    [Function("GetAderenciaAluno")]
    public async Task<HttpResponseData> GetAderenciaAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "relatorios/aluno/{alunoId}/aderencia")] HttpRequestData req,
        string alunoId)
    {
        if (_tenantContext.IsAluno && alunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios relatórios.");

        var treinos = await _treinoRepo.QueryAsync(
            _tenantContext.TenantId, t => t.AlunoId == alunoId);
        var atividades = await _atividadeRepo.QueryAsync(
            _tenantContext.TenantId, a => a.AlunoId == alunoId && a.Status == "concluido");

        // Calculate per-treino adherence
        var porTreino = treinos.Select(treino =>
        {
            var inicio = treino.DataInicio;
            var fim = treino.DataFim ?? DateTime.UtcNow;
            var totalDias = Math.Max(1, (fim - inicio).Days);
            var totalSemanas = Math.Max(1, (int)Math.Ceiling(totalDias / 7.0));

            // Prescribed sessions = number of divisões per week
            var divisoesPorSemana = Math.Max(1, treino.Divisoes.Count);
            var totalPrescritos = totalSemanas * divisoesPorSemana;

            // Completed sessions for this treino
            var realizados = atividades.Count(a => a.TreinoId == treino.Id);

            var percentual = totalPrescritos > 0
                ? Math.Min(100, Math.Round((double)realizados / totalPrescritos * 100, 1))
                : 0;

            return new
            {
                treinoId = treino.Id,
                treinoNome = treino.Nome,
                dataInicio = treino.DataInicio,
                dataFim = treino.DataFim,
                totalPrescritos,
                realizados,
                percentual
            };
        }).ToList();

        // Global adherence
        var totalPrescritos = porTreino.Sum(t => t.totalPrescritos);
        var totalRealizados = porTreino.Sum(t => t.realizados);
        var aderenciaGlobal = totalPrescritos > 0
            ? Math.Min(100, Math.Round((double)totalRealizados / totalPrescritos * 100, 1))
            : 0;

        // Weekly trend (last 12 weeks)
        var tendenciaSemanal = atividades
            .Where(a => a.DataExecucao >= DateTime.UtcNow.AddDays(-84))
            .GroupBy(a =>
            {
                var d = a.DataExecucao.Date;
                var diff = (int)d.DayOfWeek - 1;
                if (diff < 0) diff = 6;
                return d.AddDays(-diff);
            })
            .Select(g => new { semana = g.Key, realizados = g.Count() })
            .OrderBy(x => x.semana)
            .ToList();

        return await ValidationHelper.OkAsync(req, new
        {
            alunoId,
            aderenciaGlobal,
            totalPrescritos,
            totalRealizados,
            porTreino,
            tendenciaSemanal,
            geradoEm = DateTime.UtcNow
        });
    }

    /// <summary>
    /// GET /api/relatorios/aluno/{alunoId}/evolucao-carga?exercicioId={id} — Load evolution per exercise (E7-13).
    /// </summary>
    [Function("GetEvolucaoCargaAluno")]
    public async Task<HttpResponseData> GetEvolucaoCargaAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "relatorios/aluno/{alunoId}/evolucao-carga")] HttpRequestData req,
        string alunoId)
    {
        if (_tenantContext.IsAluno && alunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios relatórios.");

        var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var exercicioId = queryParams["exercicioId"];

        var atividades = await _atividadeRepo.QueryAsync(
            _tenantContext.TenantId, a => a.AlunoId == alunoId && a.Status == "concluido");

        // Flatten all exercise executions
        var execucoes = atividades
            .OrderBy(a => a.DataExecucao)
            .SelectMany(a => a.ExerciciosExecutados
                .Where(e => e.Concluido)
                .Where(e => string.IsNullOrEmpty(exercicioId) || e.ExercicioId == exercicioId)
                .Select(e => new
                {
                    data = a.DataExecucao,
                    exercicioId = e.ExercicioId,
                    nome = e.Nome,
                    cargaMaxima = e.Series.Where(s => s.Concluida && s.Carga.HasValue).Select(s => s.Carga!.Value).DefaultIfEmpty(0).Max(),
                    cargaMedia = e.Series.Where(s => s.Concluida && s.Carga.HasValue).Select(s => s.Carga!.Value).DefaultIfEmpty(0).Average(),
                    volumeTotal = e.Series.Where(s => s.Concluida).Sum(s => (s.Carga ?? 0) * (s.Repeticoes ?? 0))
                }))
            .ToList();

        // Group by exercise
        var porExercicio = execucoes
            .GroupBy(e => new { e.exercicioId, e.nome })
            .Select(g => new
            {
                exercicioId = g.Key.exercicioId,
                nome = g.Key.nome,
                pontos = g.Select(e => new
                {
                    data = e.data,
                    cargaMaxima = e.cargaMaxima,
                    cargaMedia = Math.Round(e.cargaMedia, 1),
                    volumeTotal = Math.Round(e.volumeTotal, 1)
                }).ToList()
            })
            .ToList();

        return await ValidationHelper.OkAsync(req, new { alunoId, exercicioId, porExercicio });
    }

    /// <summary>
    /// GET /api/relatorios/aluno/{alunoId}/tempo-medio — Average time per exercise (E7-14).
    /// </summary>
    [Function("GetTempoMedioAluno")]
    public async Task<HttpResponseData> GetTempoMedioAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "relatorios/aluno/{alunoId}/tempo-medio")] HttpRequestData req,
        string alunoId)
    {
        if (_tenantContext.IsAluno && alunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios relatórios.");

        var atividades = await _atividadeRepo.QueryAsync(
            _tenantContext.TenantId, a => a.AlunoId == alunoId && a.Status == "concluido");

        // Average duration per exercise (from DuracaoSegundos on ExercicioExecutado)
        var porExercicio = atividades
            .SelectMany(a => a.ExerciciosExecutados
                .Where(e => e.Concluido && e.DuracaoSegundos.HasValue && e.DuracaoSegundos > 0)
                .Select(e => new { e.ExercicioId, e.Nome, e.DuracaoSegundos }))
            .GroupBy(e => new { e.ExercicioId, e.Nome })
            .Select(g => new
            {
                exercicioId = g.Key.ExercicioId,
                nome = g.Key.Nome,
                tempoMedioSegundos = Math.Round(g.Average(e => e.DuracaoSegundos!.Value), 0),
                tempoMinSegundos = g.Min(e => e.DuracaoSegundos!.Value),
                tempoMaxSegundos = g.Max(e => e.DuracaoSegundos!.Value),
                totalExecucoes = g.Count()
            })
            .OrderByDescending(e => e.totalExecucoes)
            .ToList();

        // Average session duration
        var duracaoMediaSessao = atividades.Count > 0
            ? Math.Round(atividades.Average(a => a.DuracaoMinutos), 1)
            : 0;

        // Trend: average session duration per week (last 12 weeks)
        var tendencia = atividades
            .Where(a => a.DataExecucao >= DateTime.UtcNow.AddDays(-84))
            .GroupBy(a =>
            {
                var d = a.DataExecucao.Date;
                var diff = (int)d.DayOfWeek - 1;
                if (diff < 0) diff = 6;
                return d.AddDays(-diff);
            })
            .Select(g => new
            {
                semana = g.Key,
                duracaoMedia = Math.Round(g.Average(a => a.DuracaoMinutos), 1)
            })
            .OrderBy(x => x.semana)
            .ToList();

        return await ValidationHelper.OkAsync(req, new
        {
            alunoId,
            duracaoMediaSessao,
            porExercicio,
            tendencia,
            geradoEm = DateTime.UtcNow
        });
    }
}
