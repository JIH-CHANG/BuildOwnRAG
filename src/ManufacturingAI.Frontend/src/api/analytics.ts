import { apiClient, extractData } from "./client";
import type {
  AnalyticsOverview,
  AnalyticsRange,
  ConfidenceDistribution,
  DailyQueryCount,
  TopQuery,
} from "@/types";

export const analyticsApi = {
  overview: (rangeType: AnalyticsRange) =>
    apiClient
      .get<{ data: AnalyticsOverview }>("/v1/analytics/overview", {
        params: { rangeType },
      })
      .then(extractData),

  topQueries: (rangeType: AnalyticsRange, top = 10) =>
    apiClient
      .get<{ data: TopQuery[] }>("/v1/analytics/top-queries", {
        params: { rangeType, top: String(top) },
      })
      .then(extractData),

  confidenceDistribution: (rangeType: AnalyticsRange) =>
    apiClient
      .get<{ data: ConfidenceDistribution }>(
        "/v1/analytics/confidence-distribution",
        { params: { rangeType } }
      )
      .then(extractData),

  queryTrend: (rangeType: AnalyticsRange) =>
    apiClient
      .get<{ data: DailyQueryCount[] }>("/v1/analytics/query-trend", {
        params: { rangeType },
      })
      .then(extractData),
};
