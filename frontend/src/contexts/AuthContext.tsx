import { createContext, useContext } from 'react';
import type { ReactNode } from 'react';
import type { User } from '../types';

interface AuthContextValue {
  user: User | null;
  isLoading: boolean;
  isAuthenticated: boolean;
  login: (token: string) => Promise<void>;
  logout: () => void;
  isManagerOrAbove: boolean;
  isAdmin: boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// Auth bypass: hardcoded admin user for UI browsing.
// Replace this with the real OAuth implementation once Acumatica is configured.
const BYPASS_USER: User = {
  id: '00000000-0000-0000-0000-000000000001',
  acumaticaUserId: 'bypass',
  username: 'dev-admin',
  displayName: 'Dev Admin',
  email: 'dev@local',
  role: 'Admin',
  isActive: true,
  createdAt: new Date().toISOString(),
};

export function AuthProvider({ children }: { children: ReactNode }) {
  return (
    <AuthContext.Provider value={{
      user: BYPASS_USER,
      isLoading: false,
      isAuthenticated: true,
      login: async () => {},
      logout: () => {},
      isManagerOrAbove: true,
      isAdmin: true,
    }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
