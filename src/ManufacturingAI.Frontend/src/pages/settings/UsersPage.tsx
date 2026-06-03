import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { UserPlus } from "lucide-react";
import {
  Badge,
  Button,
  Input,
  Modal,
  Select,
  Skeleton,
  ToastContainer,
} from "@/components/ui";
import { usersApi } from "@/api/users";
import { getErrorMessage } from "@/api/client";
import { useToast } from "@/hooks/useToast";
import { formatDate } from "@/lib/utils";
import type { TenantUser } from "@/types";

const schema = z.object({
  email: z.string().email("Enter a valid email"),
  password: z.string().min(8, "Minimum 8 characters"),
  role: z.enum(["Employee", "TenantAdmin"] as const),
});

type FormValues = z.infer<typeof schema>;

export function UsersPage() {
  const qc = useQueryClient();
  const { toasts, toast, dismiss } = useToast();
  const [showModal, setShowModal] = useState(false);

  const { data, isLoading } = useQuery({
    queryKey: ["users"],
    queryFn: usersApi.list,
  });

  const statusMut = useMutation({
    mutationFn: ({
      id,
      status,
    }: {
      id: string;
      status: "Active" | "Inactive";
    }) => usersApi.setStatus(id, status),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["users"] });
      toast.success("User status updated");
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { role: "Employee" },
  });

  const createMut = useMutation({
    mutationFn: usersApi.create,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["users"] });
      toast.success("User created");
      setShowModal(false);
      reset();
    },
    onError: (err) => toast.error(getErrorMessage(err)),
  });

  const onSubmit = (values: FormValues) => createMut.mutate(values);

  return (
    <div className="max-w-3xl">
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h2 className="text-base font-semibold text-slate-100">Users</h2>
          <p className="text-sm text-slate-400">
            Manage tenant members and roles
          </p>
        </div>
        <Button size="sm" onClick={() => setShowModal(true)}>
          <UserPlus size={14} />
          Add user
        </Button>
      </div>

      {isLoading ? (
        <div className="flex flex-col gap-2">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-surface-border text-left text-xs text-slate-500">
              <th className="pb-2 pr-4 font-medium">Email</th>
              <th className="pb-2 pr-4 font-medium">Role</th>
              <th className="pb-2 pr-4 font-medium">Joined</th>
              <th className="pb-2 pr-4 font-medium">Status</th>
              <th className="pb-2 font-medium" />
            </tr>
          </thead>
          <tbody>
            {data?.items.map((user) => (
              <UserRow
                key={user.id}
                user={user}
                onToggle={() =>
                  statusMut.mutate({
                    id: user.id,
                    status:
                      user.status === "Active" ? "Inactive" : "Active",
                  })
                }
                isToggling={statusMut.isPending}
              />
            ))}
          </tbody>
        </table>
      )}

      <Modal
        open={showModal}
        onClose={() => {
          setShowModal(false);
          reset();
        }}
        title="Add user"
      >
        <form
          onSubmit={handleSubmit(onSubmit)}
          className="flex flex-col gap-4"
          noValidate
        >
          <Input
            id="email"
            type="email"
            label="Email"
            placeholder="user@factory.com"
            error={errors.email?.message}
            {...register("email")}
          />
          <Input
            id="password"
            type="password"
            label="Password"
            placeholder="Min. 8 characters"
            error={errors.password?.message}
            {...register("password")}
          />
          <Select id="role" label="Role" {...register("role")}>
            <option value="Employee">Employee</option>
            <option value="TenantAdmin">TenantAdmin</option>
          </Select>
          <div className="mt-2 flex gap-3">
            <Button type="submit" loading={isSubmitting} className="flex-1">
              Create user
            </Button>
            <Button
              type="button"
              variant="ghost"
              onClick={() => {
                setShowModal(false);
                reset();
              }}
            >
              Cancel
            </Button>
          </div>
        </form>
      </Modal>

      <ToastContainer toasts={toasts} onDismiss={dismiss} />
    </div>
  );
}

function UserRow({
  user,
  onToggle,
  isToggling,
}: {
  user: TenantUser;
  onToggle: () => void;
  isToggling: boolean;
}) {
  return (
    <tr className="border-b border-surface-border/50 hover:bg-white/[0.02]">
      <td className="py-3 pr-4 font-medium text-slate-200">{user.email}</td>
      <td className="py-3 pr-4">
        <Badge variant={user.role === "TenantAdmin" ? "info" : "muted"}>
          {user.role}
        </Badge>
      </td>
      <td className="py-3 pr-4 text-slate-400">{formatDate(user.createdAt)}</td>
      <td className="py-3 pr-4">
        <Badge variant={user.status === "Active" ? "success" : "muted"}>
          {user.status}
        </Badge>
      </td>
      <td className="py-3">
        <Button
          variant="ghost"
          size="sm"
          onClick={onToggle}
          disabled={isToggling}
        >
          {user.status === "Active" ? "Disable" : "Enable"}
        </Button>
      </td>
    </tr>
  );
}
