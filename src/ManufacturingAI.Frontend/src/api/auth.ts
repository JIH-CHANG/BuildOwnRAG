import { apiClient, extractData } from "./client";
import type { LoginResponse } from "@/types";

export const authApi = {
  login: (email: string, password: string) =>
    apiClient
      .post<{ data: LoginResponse }>("/v1/auth/login", { email, password })
      .then(extractData),

  logout: (refreshToken: string) =>
    apiClient.post("/v1/auth/logout", { refreshToken }),
};
