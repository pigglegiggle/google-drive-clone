import { useState } from "react";
import type { FormEvent } from "react";
import { api, json } from "./api";
import type { User } from "./api";

export function Login({ done }: { done: (user: User) => void }) {
  const [register, setRegister] = useState(false);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const form = new FormData(event.currentTarget);
    try {
      done(
        await api<User>(
          `/auth/${register ? "register" : "login"}`,
          json("POST", {
            email: form.get("email"),
            password: form.get("password"),
          }),
        ),
      );
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="auth">
      <div className="brand">
        Drive<span>Self-hosted file storage</span>
      </div>
      <h1>{register ? "Create an account" : "Welcome back"}</h1>
      <p className="muted">
        Your files, folders, and shared work in one place.
      </p>
      <form onSubmit={submit}>
        <label>
          Email
          <input
            type="email"
            name="email"
            autoComplete="email"
            required
            maxLength={254}
          />
        </label>
        <label>
          Password
          <input
            type="password"
            name="password"
            autoComplete={register ? "new-password" : "current-password"}
            minLength={register ? 12 : undefined}
            maxLength={128}
            required
          />
        </label>
        {register && (
          <small>
            At least 12 characters. Each account includes 10 GiB of storage.
          </small>
        )}
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
        <button className="primary" disabled={busy}>
          {busy ? "Please wait…" : register ? "Create account" : "Sign in"}
        </button>
      </form>
      <button
        className="link"
        onClick={() => {
          setRegister(!register);
          setError("");
        }}
      >
        {register
          ? "Already have an account? Sign in"
          : "New here? Create an account"}
      </button>
    </main>
  );
}
