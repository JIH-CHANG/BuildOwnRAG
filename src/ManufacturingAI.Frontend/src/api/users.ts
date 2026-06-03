import { apiClient, extractData } from "./client";
import type {
  UserListResult,
  TenantUser,
  CreateUserRequest,
  ApiKey,
  CreateApiKeyResponse,
} from "@/types";

export const usersApi = {
  list: () =>
    apiClient
      .get<{ data: UserListResult }>("/v1/users")
      .then(extractData),

  create: (data: CreateUserRequest) =>
    apiClient
      .post<{ data: TenantUser }>("/v1/users", data)
      .then(extractData),

  setStatus: (id: string, status: "Active" | "Inactive") =>
    apiClient.patch(`/v1/users/${id}/status`, { status }),
};

export const apiKeysApi = {
  list: () =>
    apiClient
      .get<{ data: ApiKey[] }>("/v1/api-keys")
      .then(extractData),

  create: (name: string) =>
    apiClient
      .post<{ data: CreateApiKeyResponse }>("/v1/api-keys", { name })
      .then(extractData),

  revoke: (id: string) => apiClient.delete(`/v1/api-keys/${id}`),
};
