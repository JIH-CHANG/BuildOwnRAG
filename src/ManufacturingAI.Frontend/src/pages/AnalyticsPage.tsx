import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  BarChart2,
  ChevronRight,
  MessageSquare,
  RefreshCw,
  TrendingUp,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge, Button, Skeleton } from "@/components/ui";
import { analyticsApi } from "@/api/analytics";
import { getErrorMessage } from "@/api/client";
import type {
  AnalyticsRange,
  ConfidenceBucket,
  DailyQueryCount,
  RecentQuery,
  RetrievedChunkDetail,
  TopQuery,
} from "@/types";

const RANGES: { value: AnalyticsRange; label: string }[] = [
  { value: "today", label: "Today" },
  { value: "week", label: "Week" },
  { value: "month", label: "Month" },
];

const formatPercent = (rate: number) => `${(rate * 100).toFixed(1)}%`;

export function AnalyticsPage() {
  const [range, setRange] = useState<AnalyticsRange>("week");

  const overview = useQuery({
    queryKey: ["analytics", "overview", range],
    queryFn: () => analyticsApi.overview(range),
  });
  const trend = useQuery({
    queryKey: ["analytics", "trend", range],
    queryFn: () => analyticsApi.queryTrend(range),
  });
  const distribution = useQuery({
    queryKey: ["analytics", "confidence", range],
    queryFn: () => analyticsApi.confidenceDistribution(range),
  });
  const topQueries = useQuery({
    queryKey: ["analytics", "top-queries", range],
    queryFn: () => analyticsApi.topQueries(range),
  });
  const recentQueries = useQuery({
    queryKey: ["analytics", "recent-queries", range],
    queryFn: () => analyticsApi.recentQueries(range),
  });

  const isFetching =
    overview.isFetching ||
    trend.isFetching ||
    distribution.isFetching ||
    topQueries.isFetching ||
    recentQueries.isFetching;

  const refetchAll = () => {
    void overview.refetch();
    void trend.refetch();
    void distribution.refetch();
    void topQueries.refetch();
    void recentQueries.refetch();
  };

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <header className="flex h-14 shrink-0 items-center justify-between border-b border-surface-border px-6">
        <h1 className="text-sm font-semibold text-slate-200">Analytics</h1>
        <div className="flex items-center gap-2">
          <RangeTabs value={range} onChange={setRange} />
          <Button
            variant="ghost"
            size="sm"
            onClick={refetchAll}
            disabled={isFetching}
            aria-label="Refresh"
          >
            <RefreshCw size={14} className={cn(isFetching && "animate-spin")} />
            Refresh
          </Button>
        </div>
      </header>

      <div className="flex-1 space-y-6 overflow-y-auto p-6">
        {/* Overview stat cards */}
        <section>
          {overview.isLoading ? (
            <CardGrid>
              {Array.from({ length: 4 }).map((_, i) => (
                <Skeleton key={i} className="h-24 w-full" />
              ))}
            </CardGrid>
          ) : overview.isError ? (
            <ErrorBox message={getErrorMessage(overview.error)} />
          ) : overview.data ? (
            <CardGrid>
              <StatCard label="Total queries" value={overview.data.totalQueries.toLocaleString()} />
              <StatCard
                label="Avg confidence"
                value={formatPercent(overview.data.averageConfidenceScore)}
              />
              <StatCard
                label="Avg latency"
                value={`${Math.round(overview.data.averageLatencyMs)} ms`}
              />
              <StatCard
                label="Positive feedback"
                value={formatPercent(overview.data.positiveFeedbackRate)}
                sub={`${formatPercent(overview.data.negativeFeedbackRate)} negative`}
              />
              <StatCard label="Total documents" value={overview.data.totalDocuments.toLocaleString()} />
              <StatCard
                label="Indexed"
                value={overview.data.indexedDocuments.toLocaleString()}
              />
              <StatCard
                label="Failed"
                value={overview.data.failedDocuments.toLocaleString()}
                emphasis={overview.data.failedDocuments > 0 ? "danger" : undefined}
              />
            </CardGrid>
          ) : null}
        </section>

        {/* Query trend + Confidence distribution */}
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
          <Panel title="Query trend" icon={<TrendingUp size={14} />}>
            {trend.isLoading ? (
              <Skeleton className="h-44 w-full" />
            ) : trend.isError ? (
              <ErrorBox message={getErrorMessage(trend.error)} />
            ) : (
              <TrendChart points={trend.data ?? []} />
            )}
          </Panel>

          <Panel title="Confidence distribution" icon={<BarChart2 size={14} />}>
            {distribution.isLoading ? (
              <Skeleton className="h-44 w-full" />
            ) : distribution.isError ? (
              <ErrorBox message={getErrorMessage(distribution.error)} />
            ) : (
              <ConfidenceBars buckets={distribution.data?.buckets ?? []} />
            )}
          </Panel>
        </div>

        {/* Top queries */}
        <Panel title="Top queries">
          {topQueries.isLoading ? (
            <div className="flex flex-col gap-2">
              {Array.from({ length: 5 }).map((_, i) => (
                <Skeleton key={i} className="h-10 w-full" />
              ))}
            </div>
          ) : topQueries.isError ? (
            <ErrorBox message={getErrorMessage(topQueries.error)} />
          ) : (
            <TopQueriesTable rows={topQueries.data ?? []} />
          )}
        </Panel>

        {/* Query inspector — question → reranked chunks + scores → answer */}
        <Panel title="Query inspector" icon={<MessageSquare size={14} />}>
          {recentQueries.isLoading ? (
            <div className="flex flex-col gap-2">
              {Array.from({ length: 5 }).map((_, i) => (
                <Skeleton key={i} className="h-12 w-full" />
              ))}
            </div>
          ) : recentQueries.isError ? (
            <ErrorBox message={getErrorMessage(recentQueries.error)} />
          ) : (
            <QueryInspector rows={recentQueries.data ?? []} />
          )}
        </Panel>
      </div>
    </div>
  );
}

// ── Range selector ────────────────────────────────────────────────────────────

function RangeTabs({
  value,
  onChange,
}: {
  value: AnalyticsRange;
  onChange: (range: AnalyticsRange) => void;
}) {
  return (
    <div className="flex rounded-md border border-surface-border bg-surface-muted p-0.5">
      {RANGES.map((r) => (
        <button
          key={r.value}
          onClick={() => onChange(r.value)}
          className={cn(
            "rounded px-3 py-1 text-xs font-medium transition-colors",
            value === r.value
              ? "bg-accent text-white"
              : "text-slate-400 hover:text-slate-200"
          )}
        >
          {r.label}
        </button>
      ))}
    </div>
  );
}

// ── Stat cards ────────────────────────────────────────────────────────────────

function CardGrid({ children }: { children: React.ReactNode }) {
  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
      {children}
    </div>
  );
}

function StatCard({
  label,
  value,
  sub,
  emphasis,
}: {
  label: string;
  value: string;
  sub?: string;
  emphasis?: "danger";
}) {
  return (
    <div className="rounded-lg border border-surface-border bg-surface-muted p-4">
      <p className="text-xs text-slate-500">{label}</p>
      <p
        className={cn(
          "mt-1 text-2xl font-semibold tabular-nums",
          emphasis === "danger" ? "text-red-400" : "text-slate-100"
        )}
      >
        {value}
      </p>
      {sub && <p className="mt-0.5 text-xs text-slate-500">{sub}</p>}
    </div>
  );
}

// ── Panel wrapper ─────────────────────────────────────────────────────────────

function Panel({
  title,
  icon,
  children,
}: {
  title: string;
  icon?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-lg border border-surface-border bg-surface-muted p-4">
      <div className="mb-4 flex items-center gap-2 text-sm font-medium text-slate-300">
        {icon}
        {title}
      </div>
      {children}
    </div>
  );
}

function ErrorBox({ message }: { message: string }) {
  return (
    <div className="rounded-md border border-red-600/30 bg-red-900/20 px-3 py-2 text-sm text-red-300">
      {message}
    </div>
  );
}

// ── Query trend chart (hand-rolled inline SVG) ────────────────────────────────

const CHART_W = 600;
const CHART_H = 160;
const CHART_PAD = 24;

function TrendChart({ points }: { points: DailyQueryCount[] }) {
  if (points.length === 0) {
    return <EmptyState message="No queries in this period" />;
  }

  const maxCount = Math.max(1, ...points.map((p) => p.count));
  const innerW = CHART_W - CHART_PAD * 2;
  const innerH = CHART_H - CHART_PAD * 2;

  // A single point can't form a line; place it in the middle.
  const x = (i: number) =>
    points.length === 1
      ? CHART_W / 2
      : CHART_PAD + (innerW * i) / (points.length - 1);
  const y = (count: number) =>
    CHART_PAD + innerH - (innerH * count) / maxCount;

  const linePoints = points.map((p, i) => `${x(i)},${y(p.count)}`).join(" ");
  const areaPoints = `${CHART_PAD},${CHART_PAD + innerH} ${linePoints} ${
    points.length === 1 ? CHART_W / 2 : CHART_PAD + innerW
  },${CHART_PAD + innerH}`;

  const total = points.reduce((sum, p) => sum + p.count, 0);
  const labelEvery = Math.max(1, Math.ceil(points.length / 6));

  return (
    <div>
      <svg
        viewBox={`0 0 ${CHART_W} ${CHART_H}`}
        className="h-44 w-full"
        preserveAspectRatio="none"
        role="img"
        aria-label="Query trend over time"
      >
        <polygon points={areaPoints} className="fill-accent/15" />
        <polyline
          points={linePoints}
          className="fill-none stroke-accent"
          strokeWidth={2}
          vectorEffect="non-scaling-stroke"
        />
        {points.map((p, i) => (
          <circle
            key={p.date}
            cx={x(i)}
            cy={y(p.count)}
            r={2.5}
            className="fill-accent"
          />
        ))}
      </svg>
      <div className="mt-2 flex justify-between text-[10px] text-slate-500">
        {points.map((p, i) =>
          i % labelEvery === 0 || i === points.length - 1 ? (
            <span key={p.date}>{formatDayLabel(p.date)}</span>
          ) : null
        )}
      </div>
      <p className="mt-2 text-xs text-slate-500">
        {total.toLocaleString()} queries total
      </p>
    </div>
  );
}

function formatDayLabel(date: string) {
  const d = new Date(date);
  if (Number.isNaN(d.getTime())) return date;
  return d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
}

// ── Confidence distribution (CSS bars) ────────────────────────────────────────

function ConfidenceBars({ buckets }: { buckets: ConfidenceBucket[] }) {
  const hasData = buckets.some((b) => b.count > 0);
  if (!hasData) {
    return <EmptyState message="No queries in this period" />;
  }

  return (
    <div className="flex flex-col gap-3">
      {buckets.map((b) => (
        <div key={b.range} className="flex items-center gap-3">
          <span className="w-16 shrink-0 text-xs tabular-nums text-slate-400">
            {b.range}
          </span>
          <div className="h-5 flex-1 overflow-hidden rounded bg-surface-border/40">
            <div
              className="h-full rounded bg-accent/70"
              style={{ width: `${b.percentage}%` }}
            />
          </div>
          <span className="w-20 shrink-0 text-right text-xs tabular-nums text-slate-400">
            {b.count} ({b.percentage.toFixed(0)}%)
          </span>
        </div>
      ))}
    </div>
  );
}

// ── Top queries table ─────────────────────────────────────────────────────────

function confidenceVariant(score: number): "success" | "warning" | "muted" {
  if (score >= 0.7) return "success";
  if (score >= 0.4) return "warning";
  return "muted";
}

function TopQueriesTable({ rows }: { rows: TopQuery[] }) {
  if (rows.length === 0) {
    return <EmptyState message="No queries yet" />;
  }

  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b border-surface-border text-left text-xs text-slate-500">
          <th className="pb-2 pr-4 font-medium">Question</th>
          <th className="pb-2 pr-4 text-right font-medium">Count</th>
          <th className="pb-2 pr-4 text-right font-medium">Avg confidence</th>
          <th className="pb-2 text-right font-medium">Positive rate</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row, i) => (
          <tr
            key={`${row.question}-${i}`}
            className="border-b border-surface-border/50 hover:bg-white/[0.02]"
          >
            <td className="max-w-md py-3 pr-4">
              <span className="block truncate text-slate-200" title={row.question}>
                {row.question}
              </span>
            </td>
            <td className="py-3 pr-4 text-right tabular-nums text-slate-400">
              {row.count.toLocaleString()}
            </td>
            <td className="py-3 pr-4 text-right">
              <Badge variant={confidenceVariant(row.averageConfidenceScore)}>
                {formatPercent(row.averageConfidenceScore)}
              </Badge>
            </td>
            <td className="py-3 text-right tabular-nums text-slate-400">
              {formatPercent(row.positiveRate)}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

// ── Query inspector ───────────────────────────────────────────────────────────

function QueryInspector({ rows }: { rows: RecentQuery[] }) {
  const [openId, setOpenId] = useState<string | null>(null);

  if (rows.length === 0) {
    return <EmptyState message="No queries in this period" />;
  }

  return (
    <div className="flex flex-col divide-y divide-surface-border/50">
      {rows.map((row) => {
        const isOpen = openId === row.id;
        return (
          <div key={row.id}>
            <button
              onClick={() => setOpenId(isOpen ? null : row.id)}
              className="flex w-full items-center gap-3 py-3 text-left hover:bg-white/[0.02]"
            >
              <ChevronRight
                size={14}
                className={cn(
                  "shrink-0 text-slate-500 transition-transform",
                  isOpen && "rotate-90"
                )}
              />
              <span className="min-w-0 flex-1 truncate text-sm text-slate-200">
                {row.question}
              </span>
              {row.feedback && (
                <Badge
                  variant={row.feedback === "Positive" ? "success" : "muted"}
                >
                  {row.feedback}
                </Badge>
              )}
              <Badge variant={confidenceVariant(row.confidenceScore)}>
                {formatPercent(row.confidenceScore)}
              </Badge>
              <span className="shrink-0 text-xs tabular-nums text-slate-500">
                {row.latencyMs} ms
              </span>
              <span className="hidden shrink-0 text-xs tabular-nums text-slate-500 sm:inline">
                {formatTimestamp(row.createdAt)}
              </span>
            </button>

            {isOpen && (
              <div className="space-y-4 pb-4 pl-7 pr-2">
                <div>
                  <p className="mb-1 text-xs font-medium text-slate-500">
                    Answer
                  </p>
                  <p className="whitespace-pre-wrap rounded-md border border-surface-border bg-surface/40 p-3 text-sm text-slate-300">
                    {row.answer}
                  </p>
                </div>

                <div>
                  <p className="mb-2 text-xs font-medium text-slate-500">
                    Retrieved chunks ({row.chunks.length})
                  </p>
                  {row.chunks.length === 0 ? (
                    <p className="text-xs text-slate-500">
                      No chunks recorded for this query.
                    </p>
                  ) : (
                    <ChunkScoreList chunks={row.chunks} />
                  )}
                </div>
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

function ChunkScoreList({ chunks }: { chunks: RetrievedChunkDetail[] }) {
  // Normalize each score type against the max within this query so the bars are
  // comparable (RRF fusion scores are tiny in absolute terms).
  const maxFusion = Math.max(...chunks.map((c) => c.fusionScore ?? 0), 1e-9);

  return (
    <div className="flex flex-col gap-2">
      {chunks.map((c) => (
        <div
          key={c.chunkId}
          className="rounded-md border border-surface-border bg-surface/40 p-3"
        >
          <div className="flex items-center gap-2">
            <span className="shrink-0 rounded bg-surface-border/60 px-1.5 py-0.5 text-[10px] font-medium tabular-nums text-slate-400">
              #{c.rank}
            </span>
            <span className="min-w-0 flex-1 truncate text-xs text-slate-300">
              {c.sourceTitle || "(untitled source)"}
            </span>
          </div>

          <p className="mt-1.5 line-clamp-3 text-xs text-slate-500">
            {c.contentExcerpt}
          </p>

          <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1">
            <ScoreTag label="Vector" value={c.vectorScore} />
            <ScoreTag label="BM25" value={c.bm25Score} />
            <ScoreTag label="Fusion" value={c.fusionScore} />
            {c.fusionScore != null && (
              <div className="ml-auto h-1.5 w-24 overflow-hidden rounded bg-surface-border/40">
                <div
                  className="h-full rounded bg-accent/70"
                  style={{ width: `${(c.fusionScore / maxFusion) * 100}%` }}
                />
              </div>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}

function ScoreTag({ label, value }: { label: string; value: number | null }) {
  return (
    <span className="text-[11px] tabular-nums text-slate-500">
      {label}:{" "}
      <span className="text-slate-300">
        {value == null ? "—" : value.toFixed(4)}
      </span>
    </span>
  );
}

function formatTimestamp(iso: string) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString("en-US", {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

// ── Shared empty state ────────────────────────────────────────────────────────

function EmptyState({ message }: { message: string }) {
  return (
    <div className="flex h-32 items-center justify-center text-sm text-slate-500">
      {message}
    </div>
  );
}
