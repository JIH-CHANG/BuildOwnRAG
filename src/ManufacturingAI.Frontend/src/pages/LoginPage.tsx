import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useNavigate } from "react-router-dom";
import { Button, Input } from "@/components/ui";
import { useAuthStore } from "@/stores/authStore";
import { authApi } from "@/api/auth";
import { getErrorMessage } from "@/api/client";

const schema = z.object({
  email: z.string().email("Enter a valid email"),
  password: z.string().min(1, "Password is required"),
});

type FormValues = z.infer<typeof schema>;

export function LoginPage() {
  const navigate = useNavigate();
  const login = useAuthStore((s) => s.login);

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  const onSubmit = async (values: FormValues) => {
    try {
      const data = await authApi.login(values.email, values.password);
      login(data.accessToken, data.user);
      navigate("/chat", { replace: true });
    } catch (err) {
      setError("root", { message: getErrorMessage(err) });
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-surface px-4">
      <div className="w-full max-w-sm rounded-xl border border-surface-border bg-surface-muted p-8 shadow-2xl">
        <div className="mb-8">
          <h1 className="text-2xl font-bold tracking-tight text-slate-100">
            BuildOwn<span className="text-accent">RAG</span>
          </h1>
          <p className="mt-1 text-sm text-slate-400">
            Sign in to your account
          </p>
        </div>

        <form onSubmit={handleSubmit(onSubmit)} noValidate className="flex flex-col gap-4">
          <Input
            id="email"
            type="email"
            label="Email"
            autoComplete="email"
            placeholder="you@factory.com"
            error={errors.email?.message}
            {...register("email")}
          />
          <Input
            id="password"
            type="password"
            label="Password"
            autoComplete="current-password"
            placeholder="••••••••"
            error={errors.password?.message}
            {...register("password")}
          />

          {errors.root && (
            <p className="rounded-md bg-red-500/10 px-3 py-2 text-sm text-red-400">
              {errors.root.message}
            </p>
          )}

          <Button type="submit" loading={isSubmitting} className="mt-2">
            {isSubmitting ? "Signing in…" : "Sign in"}
          </Button>
        </form>
      </div>
    </div>
  );
}
