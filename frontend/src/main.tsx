import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { api } from "./api";
import type { User } from "./api";
import { Login } from "./Login";
import { PublicFile } from "./PublicFile";
import { Workspace } from "./Workspace";
import "./styles.css";

function App() {
  const [user, setUser] = useState<User | null>();
  const token = location.pathname.match(/^\/share\/([a-f0-9]{64})$/)?.[1];
  useEffect(() => {
    if (!token)
      api<User>("/auth/me")
        .then(setUser)
        .catch(() => setUser(null));
  }, [token]);
  if (token) return <PublicFile token={token} />;
  if (user === undefined) return <main className="auth">Loading Drive…</main>;
  return user ? (
    <Workspace key={user.id} user={user} logout={() => setUser(null)} />
  ) : (
    <Login done={setUser} />
  );
}
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
