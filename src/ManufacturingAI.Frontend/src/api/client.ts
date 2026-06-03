import axios from "axios";
import { useAuthStore } from "@/stores/authStore";

export const apiClient = axios.create({
  baseURL: "/api",
  headers: { "Content-Type": "application/json" },
});

apiClient.interceptors.request.use((config) => {
  const token = useAuthStore.getState().token;
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (axios.isAxiosError(error) && error.response?.status === 401) {
      useAuthStore.getState().logout();
      window.location.href = "/login";
    }
    return Promise.reject(error);
  }
);

export function extractData<T>(response: { data: { data: T } }): T {
  return response.data.data;
}

export function getErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error)) {
    const serverMsg = (error.response?.data as { error?: string })?.error;
    return serverMsg ?? error.message;
  }
  if (error instanceof Error) return error.message;
  return "An unexpected error occurred";
}
